using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using API.Assembly;
using API.Events;
using Carbon;
using Carbon.Components;
using Carbon.Pooling;
using Carbon.Profiler;
using Facepunch;
using Facepunch.Extend;
using Loaders;
using Mono.Cecil;
using Utility;
using Logger = Utility.Logger;

/*
 *
 * Copyright (c) 2022-2024 Carbon Community
 * All rights reserved.
 *
 */

namespace Components;
#pragma warning disable IDE0051

internal sealed class ModuleManager : AddonManager
{
	private static readonly string[] _references =
	{
		Context.CarbonModules,
		Context.CarbonExtensions,
		Context.CarbonHarmonyMods,
		Context.CarbonManaged,
		Context.CarbonLib,
		Context.GameManaged
	};

	public static Dictionary<string, Assembly> ModuleAssemblyCache = new();
	public static Resolver ResolverInstance;
	public static ReaderParameters ReadingParameters = new() { AssemblyResolver = ResolverInstance = new Resolver()};

	public class Resolver : IAssemblyResolver
	{
		internal Dictionary<string, AssemblyDefinition> Cache = new();

		public void Dispose()
		{
			Cache.Clear();
			Cache = null;
		}

		public AssemblyDefinition Resolve(AssemblyNameReference name)
		{
			if (Cache.TryGetValue(name.Name, out var assembly))
			{
				return assembly;
			}

			var found = false;
			foreach(var directory in _references)
			{
				foreach(var file in Directory.GetFiles(directory))
				{
					switch (Path.GetExtension(file))
					{
						case ".dll":
							if (Path.GetFileNameWithoutExtension(file) == name.Name)
							{
								Cache.Add(name.Name, assembly = AssemblyDefinition.ReadAssembly(file, ReadingParameters));
								found = true;
							}
							break;
					}

					if (found) break;
				}

				if (found) break;
			}

			return assembly;
		}

		public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
		{
			return Resolve(name);
		}
	}

	internal void Awake()
	{
		Carbon.Bootstrap.Watcher.Watch(Watcher = new WatchFolder
		{
			Extension = "*.dll",
			IncludeSubFolders = false,
			Directory = Context.CarbonModules,

			OnFileCreated = (sender, file) =>
			{
				if (!Watcher.InitialEvent && !Community.Runtime.Config.Watchers.ModuleWatchers)
				{
					return;
				}

				Load(file, "ModuleManager.Created");
			},
			OnFileChanged = (sender, file) =>
			{
				if (!Community.Runtime.Config.Watchers.ModuleWatchers)
				{
					return;
				}

				Unload(file, "ModuleManager.Changed");
				Load(file, "ModuleManager.Changed");
			},
			OnFileDeleted = (sender, file) =>
			{
				if (!Community.Runtime.Config.Watchers.ModuleWatchers)
				{
					return;
				}

				Unload(file, "ModuleManager.Deleted");
			}
		});
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	public override Assembly Load(string file, string requester = null)
	{
		var item = _loaded.FirstOrDefault(x => x.File == file);

		if (item != null)
		{
			if (item.CanHotload)
			{
				var arg = new ModuleEventArgs(item.File, item.Addon as IModulePackage, null);

                try
                {
                	item.Addon.OnUnloaded(EventArgs.Empty);

                	Carbon.Bootstrap.Events
                		.Trigger(CarbonEvent.ModuleUnloaded, arg);
                }
                catch (Exception ex)
                {
                	Logger.Error($"Couldn't unload module '{item.File}'", ex);

                	Carbon.Bootstrap.Events
                		.Trigger(CarbonEvent.ModuleUnloadFailed, arg);
                }
			}
			else
			{
				Logger.Warn($"Module '{Path.GetFileName(item.File)}' does not support hotloading.");
				return null;
			}
		}

		var definition = (AssemblyDefinition)null;
		var stream = (MemoryStream)null;
		var module = (IModulePackage)null;
		var assemblyName = string.Empty;
		var result = (Assembly)null;

		if (File.Exists(file))
		{
			switch (Path.GetExtension(file))
			{
				case ".dll":
					stream = new MemoryStream(File.ReadAllBytes(file));

					var assembly = AssemblyDefinition.ReadAssembly(stream, ReadingParameters);
					assemblyName = assembly.Name.Name;

					assembly.Name.Name = $"{assembly.Name.Name}_{Guid.NewGuid()}";

					foreach (var reference in assembly.MainModule.AssemblyReferences)
					{
						if (ResolverInstance.Cache.TryGetValue(reference.Name, out var assemblyDefinition))
						{
							reference.Name = assemblyDefinition.Name.Name;
						}
					}

					ResolverInstance.Cache[assemblyName] = assembly;

					definition = assembly;
					break;
			}
		}

		if (definition == null || string.IsNullOrEmpty(assemblyName))
		{
			Dispose();
			return null;
		}

		using MemoryStream memoryStream = new MemoryStream();
		definition.Write(memoryStream);
		memoryStream.Position = 0;
		definition.Dispose();

		var bytes = memoryStream.ToArray();
		result = Assembly.Load(bytes);
		ModuleAssemblyCache[result.FullName] = result;

		MonoProfiler.TryStartProfileFor(MonoProfilerConfig.ProfileTypes.Module, result, Path.GetFileNameWithoutExtension(file));

		if (AssemblyManager.IsType<IModulePackage>(result, out var types))
		{
			var moduleFile = Path.Combine(Context.CarbonModules, $"{assemblyName}.dll");

			if (item == null)
			{
				_loaded.Add(item = new() { File = moduleFile });
			}

			item.PostProcessedRaw = bytes;
			item.Shared = result.GetTypes();

			var moduleTypes = new List<Type>();
			foreach (var type in types)
			{
				if (!type.GetInterfaces().Contains(typeof(IModulePackage))) continue;

				module = Activator.CreateInstance(type) as IModulePackage;

				Hydrate(result, module);

				moduleTypes.Add(type);
				item.Addon = module;

				Logger.Debug($"A new instance of '{type}' created");
			}

			item.Types = moduleTypes;
		}

		if (module == null)
		{
			Logger.Error($"Failed loading module '{file}'");

			Dispose();
			return null;
		}

		try
		{
			var arg = new CarbonEventArgs(file);
			var isHotloadable = item.Addon.GetType().HasAttribute(typeof(HotloadableAttribute));
			item.CanHotload = isHotloadable;

			module.Awake(arg);
			module.OnLoaded(arg);

			Carbon.Bootstrap.Events
				.Trigger(CarbonEvent.ModuleLoaded, new ModuleEventArgs(file, module, item.Shared));
		}
		catch (Exception e)
		{
			Logger.Error($"Failed to instantiate module from type '{assemblyName}' [{file}]", e);

			Carbon.Bootstrap.Events
				.Trigger(CarbonEvent.ModuleLoadFailed, new ModuleEventArgs(file, module, item.Shared));
		}

		Dispose();

		void Dispose()
		{
			stream?.Dispose();
		}

		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	public override void Unload(string file, string requester)
	{
		var item = _loaded.FirstOrDefault(x => x.File == file);

		if (item == null)
		{
			Logger.Log($"Couldn't find module '{file}' (requested by {requester})");
			return;
		}

		try
		{
			if (!item.CanHotload)
			{
				return;
			}

			Carbon.Bootstrap.Events
				.Trigger(CarbonEvent.ModuleUnloaded, new ModuleEventArgs(file, (IModulePackage)item.Addon, null));

			item.Addon.OnUnloaded(EventArgs.Empty);
		}
		catch (Exception ex)
		{
			Logger.Error($"Failed unloading module '{file}' (requested by {requester})", ex);

			Carbon.Bootstrap.Events
				.Trigger(CarbonEvent.ModuleUnloadFailed, new ModuleEventArgs(file, (IModulePackage)item.Addon, null));
		}

		_loaded.Remove(item);
	}

	internal override void Hydrate(Assembly assembly, ICarbonAddon addon)
	{
		base.Hydrate(assembly, addon);

		BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

		Type logger = typeof(API.Logger.ILogger) ?? throw new Exception();
		Type events = typeof(API.Events.IEventManager) ?? throw new Exception();

		foreach (Type type in assembly.GetTypes())
		{
			foreach (FieldInfo item in type.GetFields(flags)
				.Where(x => logger.IsAssignableFrom(x.FieldType)))
			{
				item.SetValue(assembly,
					Activator.CreateInstance(HarmonyLib.AccessTools.TypeByName("Carbon.Logger") ?? null));
			}

			foreach (FieldInfo item in type.GetFields(flags)
				.Where(x => events.IsAssignableFrom(x.FieldType)))
			{
				item.SetValue(assembly, Carbon.Bootstrap.Events);
			}
		}
	}
}
