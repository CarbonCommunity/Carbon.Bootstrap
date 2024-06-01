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

	public class Resolver : IAssemblyResolver
	{
		internal Dictionary<string, AssemblyDefinition> _cache = new();

		public void Dispose()
		{
			_cache.Clear();
			_cache = null;
		}

		public AssemblyDefinition Resolve(AssemblyNameReference name)
		{
			if (!_cache.TryGetValue(name.Name, out var assembly))
			{
				var found = false;
				foreach (var directory in _references)
				{
					foreach (var file in Directory.GetFiles(directory))
					{
						switch (Path.GetExtension(file))
						{
							case ".dll":
								if (Path.GetFileNameWithoutExtension(file) == name.Name)
								{
									_cache.Add(name.Name, assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(file));
									found = true;
								}
								break;
						}

						if (found) break;
					}

					if (found) break;
				}
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

			OnFileCreated = (sender, file) =>
			{
				if (!Watcher.InitialEvent)
				{
					return;
				}

				CurrentExtensionType = IExtensionManager.ExtensionTypes.Extension;
				Load(file, "ExtensionManager.Created");
			},
			// OnFileChanged = (sender, file) =>
			// {
			// 	_currentProcessType = AssemblyLoader.ProcessTypes.Extension;
			// 	Load(file, "ExtensionManager.Changed");
			// },
			// OnFileDeleted = (sender, file) =>
			// {
			// 	_currentProcessType = AssemblyLoader.ProcessTypes.Extension;
			// 	Load(file, "ExtensionManager.Deleted");
			// }
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

		IReadOnlyList<string> blacklist = AssemblyManager.RefBlacklist;
		IReadOnlyList<string> whitelist = null;

		try
		{
			switch (Path.GetExtension(file))
			{
				case ".dll":
					IEnumerable<Type> types;
					Assembly asm = _loader.Load(file, requester, _directories, blacklist, whitelist, CurrentExtensionType)?.Assembly
						?? throw new ReflectionTypeLoadException(null, null, null);

					if (AssemblyManager.IsType<ICarbonExtension>(asm, out types))
					{
						Logger.Debug($"Loading extension from file '{file}'");

						var extensionTypes = new List<Type>();

						foreach (Type type in types)
						{
							try
							{
								if (Activator.CreateInstance(type) is not ICarbonExtension extension)
									throw new NullReferenceException();

								Logger.Debug($"A new instance of '{extension}' created");

								var arg = new CarbonEventArgs(file);

								extension.Awake(arg);
								extension.OnLoaded(arg);

								Carbon.Bootstrap.Events
									.Trigger(CarbonEvent.ExtensionLoaded, arg);

								extensionTypes.Add(type);
								_loaded.Add(new() { Addon = extension, Shared = asm.GetTypes(), Types = extensionTypes, File = file });
							}
							catch (Exception e)
							{
								Logger.Error($"Failed to instantiate extension from type '{type}'", e);
								continue;
							}
						}
					}

					return asm;

				// case ".drm"
				// 	LoadFromDRM();
				// 	break;

				default:
					throw new Exception("File extension not supported");
			}
		}
		catch (ReflectionTypeLoadException)
		{
			Logger.Error($"Error while loading extension from '{file}' [{requester}]");
			Logger.Error($"Either the file is corrupt or has an unsupported version.");
			return null;
		}
#if DEBUG
		catch (System.Exception e)
		{
			Logger.Error($"Failed loading extension '{file}'", e);

			return null;
		}
#else
		catch (System.Exception)
		{
			Logger.Error($"Failed loading extension '{file}'");

			return null;
		}
#endif
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	public override void Unload(string file, string requester)
	{
		var item = Loaded.FirstOrDefault(x => x.Value.Key == file);

		if (item.Key == null) return;

		switch (CurrentExtensionType)
		{
			case IExtensionManager.ExtensionTypes.HarmonyMod:
			case IExtensionManager.ExtensionTypes.HarmonyModHotload:
			{
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

				mods.Clear();
				break;
			}
			case IExtensionManager.ExtensionTypes.Extension:
				break;

			default:
			case IExtensionManager.ExtensionTypes.Default:
				break;
		}

		_loaded.RemoveAll(x => x.File == item.Value.Key);
	}
}
