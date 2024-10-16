﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using API.Abstracts;
using API.Assembly;
using Loaders;

namespace Components;
#pragma warning disable IDE0051

internal abstract class AddonManager : CarbonBehaviour, IAddonManager
{
	internal class Item
	{
		public byte[] PostProcessedRaw { get; internal set; }
		public ICarbonAddon Addon { get; internal set; }
		public IReadOnlyList<Type> Types { get; internal set; }
		public IReadOnlyList<Type> Shared { get; internal set; }
		public string File { get; internal set; }
		public bool CanHotload { get; internal set; }
	}

	internal readonly AssemblyLoader _loader = new();

	internal IAssemblyManager AssemblyManager { get => GetComponentInParent<IAssemblyManager>(); }

	internal List<Item> _loaded { get; set; } = new();

	public WatchFolder Watcher { get; internal set; }

	public IReadOnlyDictionary<Type, KeyValuePair<string, byte[]>> Loaded
	{
		get
		{
			var dictionary = new Dictionary<Type, KeyValuePair<string, byte[]>>();
			foreach (var item in _loaded)
			{
				foreach (var type in item.Types)
				{
					if (!dictionary.ContainsKey(type))
					{
						dictionary.Add(type, new KeyValuePair<string, byte[]>(item.File, item.PostProcessedRaw));
					}
				}
			}

			return dictionary;
		}
	}
	public IReadOnlyDictionary<Type, string> Shared
	{
		get
		{
			var dictionary = new Dictionary<Type, string>();
			foreach (var item in _loaded)
			{
				foreach (var type in item.Shared)
				{
					if (!dictionary.ContainsKey(type))
					{
						dictionary.Add(type, item.File);
					}
				}
			}

			return dictionary;
		}
	}

	public byte[] Read(string file)
		=> _loader.ReadFromCache(file).Raw;

	public abstract Assembly Load(string file, string requester);
	public abstract void Unload(string file, string requester);

	internal virtual void Hydrate(Assembly assembly, ICarbonAddon addon)
	{
		// BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

		// foreach (Type type in assembly.GetTypes())
		// {
		// 	foreach (MethodInfo method in type.GetMethods(flags))
		// 	{
		// 		// Community.Runtime.HookManager.IsHookLoaded(method.Name)
		// 		// Community.Runtime.HookManager.Subscribe(method.Name, Name);
		// 	}
		// }
	}
}
