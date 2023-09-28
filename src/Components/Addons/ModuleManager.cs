using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using API.Assembly;
using API.Events;
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
	internal bool _hasLoaded;

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
			if(!_cache.TryGetValue(name.Name, out var assembly))
			{
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
			Directory = Context.CarbonModules,

			OnFileCreated = (sender, file) =>
			{
				Reload(file, "ModuleManager.Created");
			},
			OnFileChanged = (sender, file) =>
			{
				Reload(file, "ModuleManager.Changed");
			},
			OnFileDeleted = (sender, file) =>
			{
				Reload(file, "ModuleManager.Deleted");
			}
		});
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
					IAssemblyCache cache = _loader.Load(file, requester, _directories, blacklist, whitelist);
					Assembly asm = cache?.Assembly
						?? throw new ReflectionTypeLoadException(null, null, null);

					if (AssemblyManager.IsType<ICarbonModule>(asm, out types))
					{
						Logger.Debug($"Loading module from file '{file}'");

						var moduleTypes = new List<Type>();	

						foreach (Type type in types)
						{
							try
							{
								if (Activator.CreateInstance(type) is not ICarbonModule module)
									throw new NullReferenceException();

								Logger.Debug($"A new instance of '{module}' created");
								Hydrate(asm, module);

								module.Awake(EventArgs.Empty);
								module.OnLoaded(EventArgs.Empty);

								// for now force all modules to be enabled when loaded
								module.OnEnable(EventArgs.Empty);

								Carbon.Bootstrap.Events
									.Trigger(CarbonEvent.ModuleLoaded, new ModuleEventArgs(file, module, types));

								moduleTypes.Add(type);
								_loaded.Add(new() { Addon = module, PostProcessedRaw = cache.Raw, Shared = asm.GetTypes(), Types = moduleTypes, File = file });
							}
							catch (Exception e)
							{
								Logger.Error($"Failed to instantiate module from type '{type}'", e);
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
			Logger.Error($"Error while loading module from '{file}'.");
			Logger.Error($"Either the file is corrupt or has an unsupported version.");
			return null;
		}
#if DEBUG
		catch (System.Exception e)
		{
			Logger.Error($"Failed loading module '{file}'", e);

			return null;
		}
#else
		catch (System.Exception)
		{
			Logger.Error($"Failed loading module '{file}'");

			return null;
		}
#endif
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
			item.Addon.OnUnloaded(EventArgs.Empty);
		}
		catch (Exception ex)
		{
			Logger.Error($"Failed unloading module '{file}' (requested by {requester})", ex);
		}

		_loaded.Remove(item);
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

		var cache = new Dictionary<string, AssemblyDefinition>();
		var streams = new List<MemoryStream>();
		var modules = new Dictionary<string, ICarbonModule>();

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
						Load(file, "ModuleManager.Reload");
					}
					else
					{
						var stream = new MemoryStream(Process(File.ReadAllBytes(file)));
						var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(stream, new ReaderParameters { AssemblyResolver = new Resolver() });
						var originalName = assembly.Name.Name;
						var originalVersion = assembly.Name.Version;
						// assembly.Name = new AssemblyNameDefinition($"{assembly.Name.Name}_{Guid.NewGuid()}", assembly.Name.Version);
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

			if (AssemblyManager.IsType<ICarbonModule>(processedAssembly, out var types))
			{
				var moduleFile = Path.Combine(Context.CarbonModules, $"{_assembly.Key}.dll");
				var existentItem = _loaded.FirstOrDefault(x => x.File == moduleFile);
				if (existentItem == null)
				{
					_loaded.Add(existentItem = new() { File = moduleFile });
				}

				existentItem.PostProcessedRaw = bytes;
				existentItem.Shared = processedAssembly.GetTypes();

				var moduleTypes = new List<Type>();
				foreach (var type in types)
				{
					if (Activator.CreateInstance(type) is ICarbonModule module)
					{
						Hydrate(processedAssembly, module);

						moduleTypes.Add(type);
						existentItem.Addon = module;

						Logger.Debug($"A new instance of '{type}' created");
						modules.Add(_assembly.Key, module);
					}
				}

				existentItem.Types = moduleTypes;
			}
		}

		foreach (var module in modules)
		{
			try
			{
				var moduleFile = Path.Combine(Context.CarbonModules, $"{module.Key}.dll");
				var arg = new CarbonEventArgs(moduleFile);
				var existentItem = _loaded.FirstOrDefault(x => x.File == moduleFile);

				module.Value.Awake(arg);
				module.Value.OnLoaded(arg);

				// for now force all modules to be enabled when loaded
				module.Value.OnEnable(arg);

				Carbon.Bootstrap.Events
					.Trigger(CarbonEvent.ModuleLoaded, new ModuleEventArgs(moduleFile, module.Value, existentItem.Shared));
			}
			catch (Exception e)
			{
				Logger.Error($"Failed to instantiate module from type '{module.Value}'", e);
				continue;
			}
		}

		Dispose();

		void Dispose()
		{
			foreach (var stream in streams)
			{
				stream.Dispose();
			}

			streams.Clear();
			modules.Clear();
			cache.Clear();
			streams = null;
			modules = null;
			cache = null;
		}
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
