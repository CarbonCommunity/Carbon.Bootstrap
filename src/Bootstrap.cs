﻿using System;
using System.IO;
using System.Reflection;
using API.Events;
using Components;
using Patches;
using Utility;

namespace Carbon;

public sealed class Bootstrap
{
	private static readonly string identifier;
	private static readonly string assemblyName;
	private static UnityEngine.GameObject _gameObject;
	private static HarmonyLib.Harmony _harmonyInstance;

	public static string Name { get => assemblyName; }

	internal static HarmonyLib.Harmony Harmony { get => _harmonyInstance; }

	internal static AnalyticsManager Analytics { get; private set; }

	internal static AssemblyManager AssemblyEx { get; private set; }

	internal static CommandManager Commands { get; private set; }

	internal static DownloadManager Downloader { get; private set; }

	internal static EventManager Events { get; private set; }

	internal static PermissionManager Permissions { get; private set; }

#if EXPERIMENTAL
	internal static ThreadManager Threads { get; private set; }
#endif

	internal static FileWatcherManager Watcher { get; private set; }


	static Bootstrap()
	{
		Carbon.Components.ConVarSnapshots.TakeSnapshot();

		identifier = $"{Guid.NewGuid():N}";
		Utility.Logger.Warn($"Using '{identifier}' as runtime namespace");
		assemblyName = Assembly.GetExecutingAssembly().GetName().Name;
	}

	public static void Initialize()
	{
		Utility.Logger.Log($"{assemblyName} loaded.");
		_harmonyInstance = new HarmonyLib.Harmony(identifier);

		var logPath = Path.Combine(Context.CarbonLogs, "Carbon.Harmony.log");

		Environment.SetEnvironmentVariable("HARMONY_LOG_FILE", logPath);
		typeof(HarmonyLib.FileLog).GetField("_logPathInited", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, false);
#if DEBUG
		HarmonyLib.Harmony.DEBUG = true;
#elif RELEASE
		HarmonyLib.Harmony.DEBUG = false;
#endif

		if(File.Exists(logPath))
		{
			File.Delete(logPath);
		}

		_gameObject = new UnityEngine.GameObject("Carbon");
		UnityEngine.Object.DontDestroyOnLoad(_gameObject);

		// top priority
		Commands = _gameObject.AddComponent<CommandManager>();
		Events = _gameObject.AddComponent<EventManager>();
		Watcher = _gameObject.AddComponent<FileWatcherManager>();

		// standard priority
		Analytics = _gameObject.AddComponent<AnalyticsManager>();
		AssemblyEx = _gameObject.AddComponent<AssemblyManager>();
		Downloader = _gameObject.AddComponent<DownloadManager>();

		Events.Subscribe(CarbonEvent.StartupShared, x =>
		{
			AssemblyEx.Components.Load("Carbon.dll", "CarbonEvent.StartupShared");
		});

		Events.Subscribe(CarbonEvent.CarbonStartupComplete, x =>
		{
			Watcher.enabled = true;
		});

		try
		{
			Utility.Logger.Log("Applying Harmony patches");
			Harmony.PatchAll(Assembly.GetExecutingAssembly());
		}
		catch (Exception e)
		{
			Utility.Logger.Error("Unable to apply all patches", e);
		}

		Events.Subscribe(CarbonEvent.HooksInstalled, x =>
		{
			FileSystem_WarmupHalt.IsReady = true;
		});
	}
}
