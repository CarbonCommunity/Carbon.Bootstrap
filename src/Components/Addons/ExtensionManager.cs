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
using Carbon.Extensions;
using Carbon.Profiler;
using Facepunch.Extend;
using Loaders;
using Mono.Cecil;
using Steamworks.Data;
using UnityEngine;
using Utility;
using Logger = Utility.Logger;

/*
 *
 * Copyright (c) 2022-2023 Carbon Community
 * All rights reserved.
 *
 */

namespace Components;

#pragma warning disable IDE0051

internal sealed class ExtensionManager : AddonManager, IExtensionManager
{
	/*
	 * CARBON EXTENSIONS
	 * API.Contracts.ICarbonExtension
	 *
	 * An assembly to be considered as a Carbon Extension must:
	 *   1. Implement the ICarbonExtension interface
	 *   2. Must not change directly the world
	 *   3. Provide additional functionality such as new features or services
	 *
	 * Carbon extensions are different from Oxide extensions, in Carbon extensions
	 * are "libraries" and cannot access features such as hooks or change the
	 * world, either directly or using reflection.
	 *
	 */

	public IExtensionManager.ExtensionTypes CurrentExtensionType { get; set; }

	public WatchFolder HarmonyWatcher { get; internal set; }

	private readonly string[] _directories =
	{
		Context.CarbonExtensions,
		Context.CarbonHarmonyMods,
	};
	private static readonly string[] _references =
	{
		Context.CarbonExtensions,
		Context.CarbonHarmonyMods,
		Context.CarbonManaged,
		Context.CarbonLib,
		Context.GameManaged
	};

	public static Dictionary<string, Assembly> ExtensionAssemblyCache = new();
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
			Directory = Context.CarbonExtensions,

			OnFileCreated = (_, file) =>
			{
				if (!Watcher.InitialEvent && !Community.Runtime.Config.Watchers.ExtensionWatchers)
				{
					return;
				}

				CurrentExtensionType = IExtensionManager.ExtensionTypes.Extension;
				Load(file, "ExtensionManager.Created");
			},
			OnFileChanged = (sender, file) =>
			{
				if (!Community.Runtime.Config.Watchers.ExtensionWatchers)
				{
					return;
				}

				CurrentExtensionType = IExtensionManager.ExtensionTypes.Extension;
				Load(file, "ExtensionManager.Changed");
			},
			OnFileDeleted = (sender, file) =>
			{
				if (!Community.Runtime.Config.Watchers.ExtensionWatchers)
				{
					return;
				}

				CurrentExtensionType = IExtensionManager.ExtensionTypes.Extension;
				Load(file, "ExtensionManager.Deleted");
			}
		});

		Carbon.Bootstrap.Watcher.Watch(HarmonyWatcher = new WatchFolder
		{
			Extension = "*.dll",
			IncludeSubFolders = false,
			Directory = Context.CarbonHarmonyMods,

			OnFileCreated = (sender, file) =>
			{
				if (!HarmonyWatcher.InitialEvent && !Community.Runtime.Config.Watchers.HarmonyWatchers)
				{
					return;
				}

				if (HarmonyWatcher.InitialEvent)
				{
					CurrentExtensionType = IExtensionManager.ExtensionTypes.HarmonyMod;
				}
				else
				{
					CurrentExtensionType = IExtensionManager.ExtensionTypes.HarmonyModHotload;
				}

				Load(file, "ExtensionManager.Created");
			},
			OnFileChanged = (sender, file) =>
			{
				if (!Community.Runtime.Config.Watchers.HarmonyWatchers)
				{
					return;
				}

				CurrentExtensionType = IExtensionManager.ExtensionTypes.HarmonyMod;
				Unload(file, "ExtensionManager.HotloadUnload");

				CurrentExtensionType = IExtensionManager.ExtensionTypes.HarmonyModHotload;
				Load(file, "ExtensionManager.Changed");
			},
			OnFileDeleted = (sender, file) =>
			{
				if (!Community.Runtime.Config.Watchers.HarmonyWatchers)
				{
					return;
				}

				CurrentExtensionType = IExtensionManager.ExtensionTypes.HarmonyMod;
				Unload(file, "ExtensionManager.Deleted");
			}
		});

		Watcher.Handler.EnableRaisingEvents = false;
		HarmonyWatcher.Handler.EnableRaisingEvents = false;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	public override Assembly Load(string file, string requester = null)
	{
		if (requester is null)
		{
			MethodBase caller = new StackFrame(1).GetMethod();
			requester = $"{caller.DeclaringType}.{caller.Name}";
		}

		var item = _loaded.FirstOrDefault(x => x.File == file);

		if (item != null)
		{
			if (item.CanHotload)
			{
				var arg = new CarbonEventArgs(item.File);

				try
				{
					item.Addon.OnUnloaded(arg);

					Carbon.Bootstrap.Events
						.Trigger(CarbonEvent.ExtensionUnloaded, arg);
				}
				catch (Exception ex)
				{
					Logger.Error($"Couldn't unload extension '{item.File}'", ex);

					Carbon.Bootstrap.Events
						.Trigger(CarbonEvent.ExtensionUnloadFailed, arg);
				}
			}
			else
			{
				return null;
			}
		}

		var definition = (AssemblyDefinition)null;
		var stream = (MemoryStream)null;
		var extension = (ICarbonExtension)null;
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
		result = _loader.Load(file, requester, _directories, AssemblyManager.RefBlacklist, null, CurrentExtensionType)?.Assembly;

		ExtensionAssemblyCache[result.FullName] = result;

		MonoProfiler.TryStartProfileFor(MonoProfilerConfig.ProfileTypes.Extension, result, Path.GetFileNameWithoutExtension(file));
		Assemblies.Extensions.Update(Path.GetFileNameWithoutExtension(file), result, file);

		if (AssemblyManager.IsType<ICarbonExtension>(result, out var types))
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
				if (!type.GetInterfaces().Contains(typeof(ICarbonExtension))) continue;

				extension = Activator.CreateInstance(type) as ICarbonExtension;

				Hydrate(result, extension);

				moduleTypes.Add(type);
				item.Addon = extension;

				Logger.Debug($"A new instance of '{type}' created");
			}

			item.Types = moduleTypes;
		}

		if (extension == null)
		{
			Logger.Error($"Failed loading extension '{file}'");

			Dispose();
			return null;
		}

		try
		{
			var arg = new CarbonEventArgs(file);
			var isHotloadable = item.Addon.GetType().HasAttribute(typeof(HotloadableAttribute));
			item.CanHotload = isHotloadable;

			extension.Awake(arg);
			extension.OnLoaded(arg);

			Carbon.Bootstrap.Events
				.Trigger(CarbonEvent.ExtensionLoaded, new CarbonEventArgs(file));
		}
		catch (Exception e)
		{
			Logger.Error($"Failed to instantiate module from type '{assemblyName}' [{file}]", e);

			Carbon.Bootstrap.Events
				.Trigger(CarbonEvent.ExtensionLoadFailed, new CarbonEventArgs(file));
		}

		void Dispose()
		{
			stream?.Dispose();
		}

		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	public override void Unload(string file, string requester)
	{
		switch (CurrentExtensionType)
		{
			case IExtensionManager.ExtensionTypes.HarmonyMod:
			case IExtensionManager.ExtensionTypes.HarmonyModHotload:
			{
				var item = Loaded.FirstOrDefault(x => x.Value.Key == file);

				if (item.Key == null) return;

				if (!Harmony.ModHooks.TryGetValue(item.Key.Assembly, out var mods))
				{
					return;
				}

				foreach (var mod in mods)
				{
					try
					{
						mod.GetType().GetMethod("OnUnloaded").Invoke(mod, new object[1]);
					}
					catch (Exception ex)
					{
						Logger.Error($"Failed unloading HarmonyMod '{item.Value.Key}'", ex);
					}
				}

				var unpatchCount = Harmony.UnpatchAll(item.Key.Assembly.GetName().Name);
				Harmony.ModHooks.Remove(item.Key.Assembly);
				Logger.Log($"Unloaded '{Path.GetFileNameWithoutExtension(item.Value.Key)}' HarmonyMod with {unpatchCount:n0} {unpatchCount.Plural("patch", "patches")}");

				Assemblies.Harmony.Eliminate(Path.GetFileNameWithoutExtension(file));

				mods.Clear();

				_loaded.RemoveAll(x => x.File == item.Value.Key);
				break;
			}
			case IExtensionManager.ExtensionTypes.Extension:
			{
				var item = _loaded.FirstOrDefault(x => x.File == file);

				try
				{
					if (!item.CanHotload)
					{
						return;
					}

					Carbon.Bootstrap.Events
						.Trigger(CarbonEvent.ExtensionUnloaded, new CarbonEventArgs(file));

					item.Addon.OnUnloaded(EventArgs.Empty);
				}
				catch (Exception ex)
				{
					Logger.Error($"Failed unloading extension '{file}' (requested by {requester})", ex);

					Carbon.Bootstrap.Events
						.Trigger(CarbonEvent.ExtensionUnloadFailed, new CarbonEventArgs(file));
				}

				Assemblies.Extensions.Eliminate(Path.GetFileNameWithoutExtension(file));

				_loaded.Remove(item);

				break;
			}
			default:
			case IExtensionManager.ExtensionTypes.Default:
				break;
		}
	}
}
