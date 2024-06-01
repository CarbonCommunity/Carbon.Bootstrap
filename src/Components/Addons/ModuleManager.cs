using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using API.Assembly;
using API.Events;
using Carbon.Components;
using Carbon.Pooling;
using Carbon.Profiler;
using Facepunch;
using Facepunch.Extend;
using Loaders;
using Mono.Cecil;
using Utility;

/*
 *
 * Copyright (c) 2022-2023 Carbon Community
 * All rights reserved.
 *
 */

namespace Components;
#pragma warning disable IDE0051

internal sealed class ModuleManager : AddonManager
{
	/*
	 * CARBON MODULES
	 * API.Contracts.ICarbonModule
	 *
	 * An assembly to be considered as a Carbon Module must:
	 *   1. Be optional
	 *   2. Implement the ICarbonModule interface
	 *   3. Provide additional functionality such as new features or services
	 *
	 * Carbon modules can be compared to Oxide Extensions, they can be created
	 * by anyone and can change and/or interact with the world as any other user
	 * plugin can.
	 *
	 */
	private readonly string[] _directories =
	{
		Context.CarbonModules
	};
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
	public int Iterations = 100;

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
				Load(file, "ModuleManager.Created");
			},
			OnFileChanged = (sender, file) =>
			{
				Unload(file, "ModuleManager.Changed");
				Load(file, "ModuleManager.Changed");
			},
			OnFileDeleted = (sender, file) =>
			{
				Unload(file, "ModuleManager.Deleted");
			}
		});
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	public override Assembly Load(string file, string requester = null)
	{
		var hotloadable = true;
		var currentlyLoaded = _loaded.FirstOrDefault(x => x.File == file);

		if (currentlyLoaded != null)
		{
			if (!currentlyLoaded.Addon.GetType().HasAttribute(typeof(HotloadableAttribute)))
			{
				hotloadable = false;
			}
			else
			{
				var arg = new ModuleEventArgs(currentlyLoaded.File, currentlyLoaded.Addon as ICarbonModule, null);

				try
				{
					currentlyLoaded.Addon.OnUnloaded(EventArgs.Empty);

					Carbon.Bootstrap.Events
						.Trigger(CarbonEvent.ModuleUnloaded, arg);
				}
				catch (Exception ex)
				{
					Logger.Error($"Couldn't unload module '{currentlyLoaded.File}'", ex);

					Carbon.Bootstrap.Events
						.Trigger(CarbonEvent.ModuleUnloadFailed, arg);
				}
			}
		}

		var definition = (AssemblyDefinition)null;
		var stream = (MemoryStream)null;
		var module = (ICarbonModule)null;
		var assemblyName = string.Empty;
		var result = (Assembly)null;

		if (File.Exists(file) && hotloadable)
		{
			switch (Path.GetExtension(file))
			{
				case ".dll":
					stream = new MemoryStream(File.ReadAllBytes(file));
					var assembly = AssemblyDefinition.ReadAssembly(stream, ReadingParameters);
					assemblyName = assembly.Name.Name;

					// var version = assembly.Name.Version;
					// assembly.Name.Name = $"{assembly.Name.Name}_{Guid.NewGuid()}";
					// assembly.Name.Version = new Version(version.Major, version.Minor, version.Build + Iterations++, version.Revision);

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

		if (AssemblyManager.IsType<ICarbonModule>(result, out var types))
		{
			var moduleFile = Path.Combine(Context.CarbonModules, $"{assemblyName}.dll");
			var existentItem = _loaded.FirstOrDefault(x => x.File == moduleFile);

			if (existentItem == null)
			{
				_loaded.Add(existentItem = new() { File = moduleFile });
			}

			existentItem.PostProcessedRaw = bytes;
			existentItem.Shared = result.GetTypes();

			var moduleTypes = new List<Type>();
			foreach (var type in types)
			{
				if (!type.GetInterfaces().Contains(typeof(ICarbonModule))) continue;

				module = Activator.CreateInstance(type) as ICarbonModule;

				Hydrate(result, module);

				moduleTypes.Add(type);
				existentItem.Addon = module;

				Logger.Debug($"A new instance of '{type}' created");
			}

			existentItem.Types = moduleTypes;
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
			var existentItem = _loaded.FirstOrDefault(x => x.File == file);

			module.Awake(arg);
			module.OnLoaded(arg);

			Carbon.Bootstrap.Events
				.Trigger(CarbonEvent.ModuleLoaded, new ModuleEventArgs(file, module, existentItem.Shared));
		}
		catch (Exception e)
		{
			Logger.Error($"Failed to instantiate module from type '{assemblyName}' [{file}]", e);
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
				Logger.Warn($" Cannot hotload as the module does not support it [{file} requested by {requester}]");
				return;
			}

			Carbon.Bootstrap.Events
				.Trigger(CarbonEvent.ModuleUnloaded, new ModuleEventArgs(file, (ICarbonModule)item.Addon, null));

			item.Addon.OnUnloaded(EventArgs.Empty);
		}
		catch (Exception ex)
		{
			Logger.Error($"Failed unloading module '{file}' (requested by {requester})", ex);
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
