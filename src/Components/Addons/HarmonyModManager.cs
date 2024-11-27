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
using Facepunch;
using Facepunch.Extend;
using Loaders;
using Mono.Cecil;
using Steamworks.Data;
using UnityEngine;
using Utility;
using Logger = Utility.Logger;

namespace Components;

#pragma warning disable IDE0051

internal sealed class HarmonyModManager : AddonManager, IHarmonyModManager
{
	private readonly string[] _directories =
	{
		Context.CarbonHarmonyMods
	};
	private static readonly string[] _references =
	{
		Context.CarbonHarmonyMods,
		Context.CarbonManaged,
		Context.CarbonLib,
		Context.GameManaged
	};

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
			Directory = Context.CarbonHarmonyMods,

			OnFileCreated = (_, file) =>
			{
				if (!Watcher.InitialEvent && !Community.Runtime.Config.Watchers.HarmonyWatchers)
				{
					return;
				}

				Load(file, "HarmonyModManager.Created");
			},
			OnFileChanged = (sender, file) =>
			{
				if (!Community.Runtime.Config.Watchers.HarmonyWatchers)
				{
					return;
				}

				Load(file, "HarmonyModManager.Changed");
			},
			OnFileDeleted = (sender, file) =>
			{
				if (!Community.Runtime.Config.Watchers.HarmonyWatchers)
				{
					return;
				}

				Load(file, "HarmonyModManager.Deleted");
			}
		});

		Watcher.Handler.EnableRaisingEvents = false;
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

		var definition = (AssemblyDefinition)null;
		var stream = (MemoryStream)null;
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
		result = _loader.Load(file, requester, _directories, AssemblyManager.RefBlacklist, null, IExtensionManager.ExtensionTypes.HarmonyMod)?.Assembly;


		var isProfiled = MonoProfiler.TryStartProfileFor(MonoProfilerConfig.ProfileTypes.Extension, result, Path.GetFileNameWithoutExtension(file));
		Assemblies.Extensions.Update(Path.GetFileNameWithoutExtension(file), result, file, isProfiled);

		void Dispose()
		{
			stream?.Dispose();
		}

		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	public override void Unload(string file, string requester)
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
				mod.OnUnloaded(new OnHarmonyModUnloadedArgs());
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

		_loaded.RemoveAll(x => x.File == item.Value.Key);
	}
}
