﻿using System;
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

internal sealed class ExtensionManager : AddonManager
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

	internal bool _hasLoaded;
	internal AssemblyLoader.ProcessTypes _currentProcessType;

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
				_currentProcessType = AssemblyLoader.ProcessTypes.Extension;
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
				_currentProcessType = AssemblyLoader.ProcessTypes.HarmonyMod;
				Load(file, "ExtensionManager.Created");
			},
			// OnFileChanged = (sender, file) =>
			// {
			// 	_currentProcessType = AssemblyLoader.ProcessTypes.HarmonyMod;
			// 	Load(file, "ExtensionManager.Changed");
			// },
			// OnFileDeleted = (sender, file) =>
			// {
			// 	_currentProcessType = AssemblyLoader.ProcessTypes.HarmonyMod;
			// 	Load(file, "ExtensionManager.Deleted");
			// }
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
					Assembly asm = _loader.Load(file, requester, _directories, blacklist, whitelist, _currentProcessType)?.Assembly
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
					else
					{
						throw new Exception("Unsupported assembly type");
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

		if (item.Key != null && Harmony.ModHooks.TryGetValue(item.Key.Assembly, out var mods))
		{
			foreach (var mod in mods)
			{
				try
				{
					// var type = mod.GetType();
					// type.GetMethod("OnUnloaded").Invoke(mod, new object[1]);
				}
				catch (Exception ex)
				{
					Logger.Error($"Failed unloading HarmonyMod '{item.Value.Key}'", ex);
				}
			}

			Logger.Log($"Unloaded '{Path.GetFileNameWithoutExtension(item.Value.Key)}' HarmonyMod with {mods.Count:n0} {mods.Count.Plural("hook", "hooks")}");

			mods.Clear();

			_loaded.RemoveAll(x => x.File == item.Value.Key);

			Harmony.ModHooks.Remove(item.Key.Assembly);
		}
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	public override void Reload(string file, string requester)
	{
		var nonReloadables = new List<string>();
		var currentlyLoaded = _loaded.FirstOrDefault(x => x.File == file);

		if (currentlyLoaded != null)
		{
			if (!currentlyLoaded.Addon.GetType().HasAttribute(typeof(HotloadableAttribute)))
			{
				nonReloadables.Add(currentlyLoaded.File);
			}
			else
			{
				currentlyLoaded.Addon.OnUnloaded(EventArgs.Empty);
			}
		}

		var cache = new Dictionary<string, AssemblyDefinition>();
		var streams = new List<MemoryStream>();
		var extensions = new Dictionary<string, ICarbonExtension>();

		static byte[] Process(byte[] raw)
		{
			if (AssemblyLoader.IndexOf(raw, new byte[4] { 0x01, 0xdc, 0x7f, 0x01 }) == 0)
			{
				byte[] checksum = new byte[20];
				Buffer.BlockCopy(raw, 4, checksum, 0, 20);
				return AssemblyLoader.Package(checksum, raw, 24);
			}

			return raw;
		}

		if (File.Exists(file) && !nonReloadables.Contains(file))
		{
			switch (Path.GetExtension(file))
			{
				case ".dll":
					if (!_hasLoaded)
					{
						Load(file, "ExtensionManager.Reload");
					}
					else
					{
						var raw = File.ReadAllBytes(file);
						var convert = Community.Runtime.Compat.AttemptOxideConvert(ref raw);

						switch (convert)
						{
							case ConversionResult.Fail:
								Logger.Warn($" >> Failed Oxide extension conversion for '{file}'");
								return;
						}

						using var stream = new MemoryStream(Process(raw));
						var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(stream, new ReaderParameters { AssemblyResolver = new Resolver() });
						var originalName = assembly.Name.Name;
						assembly.Name = new AssemblyNameDefinition($"{assembly.Name.Name}_{Guid.NewGuid()}", assembly.Name.Version);
						cache.Add(originalName, assembly);
					}
					break;
			}
		}

		_hasLoaded = true;

		nonReloadables.Clear();
		nonReloadables = null;

		foreach (var _assembly in cache)
		{
			foreach (var refer in _assembly.Value.MainModule.AssemblyReferences)
			{
				if (cache.TryGetValue(refer.Name, out var assembly))
				{
					refer.Name = assembly.Name.Name;
				}
			}

			using MemoryStream memoryStream = new MemoryStream();
			_assembly.Value.Write(memoryStream);
			memoryStream.Position = 0;
			_assembly.Value.Dispose();

			var bytes = memoryStream.ToArray();
			var processedAssembly = Assembly.Load(bytes);

			if (AssemblyManager.IsType<ICarbonExtension>(processedAssembly, out var types))
			{
				var extensionFile = Path.Combine(Context.CarbonExtensions, $"{_assembly.Key}.dll");
				var existentItem = _loaded.FirstOrDefault(x => x.File == extensionFile);
				if (existentItem == null)
				{
					_loaded.Add(existentItem = new() { File = extensionFile });
				}

				existentItem.PostProcessedRaw = bytes;
				existentItem.Shared = processedAssembly.GetExportedTypes();

				var extensionTypes = new List<Type>();
				foreach (var type in types)
				{
					if (Activator.CreateInstance(type) is ICarbonExtension ext)
					{
						extensionTypes.Add(type);
						existentItem.Addon = ext;

						Logger.Debug($"A new instance of '{type}' created");
						extensions.Add(_assembly.Key, ext);
					}
				}
				existentItem.Types = extensionTypes;
			}
		}

		foreach (var extension in extensions)
		{
			var extensionFile = Path.Combine(Context.CarbonExtensions, $"{extension.Key}.dll");
			var arg = new CarbonEventArgs(extensionFile);

			try
			{
				extension.Value.Awake(arg);
				extension.Value.OnLoaded(arg);

				Carbon.Bootstrap.Events
					.Trigger(CarbonEvent.ExtensionLoaded, arg);
			}
			catch (Exception e)
			{
				Carbon.Bootstrap.Events
					.Trigger(CarbonEvent.ExtensionLoadFailed, arg);

				Logger.Error($"Failed to instantiate extension from type '{extension.Value}'\n{e}\nInner: {e.InnerException}");
				continue;
			}
		}

		_hasLoaded = true;

		Dispose();

		void Dispose()
		{
			foreach(var stream in streams)
			{
				stream.Dispose();
			}

			extensions.Clear();
			streams.Clear();
			cache.Clear();

			extensions = null;
			streams = null;
			cache = null;
		}
	}
}
