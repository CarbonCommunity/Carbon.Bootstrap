﻿using System;
using API.Events;
using HarmonyLib;

/*
 *
 * Copyright (c) 2022-2024 Carbon Community
 * All rights reserved.
 *
 */

namespace Patches;

internal static class __Bootstrap
{
	[HarmonyPatch(typeof(Bootstrap), methodName: nameof(Bootstrap.StartupShared))]
	internal static class __StartupShared
	{
		public static void Prefix()
		{
			Carbon.Bootstrap.Events
				.Trigger(CarbonEvent.StartupShared, EventArgs.Empty);
		}

		public static void Postfix()
		{
			Carbon.Bootstrap.Events
				.Trigger(CarbonEvent.StartupSharedComplete, EventArgs.Empty);

			/* example mockup --------------------------------------------------
			bool ArePluginsReady = false;
			Loader.GetInstance().Events.Subscribe(API.Events.CarbonEvent.OnPluginProcessFinished, x => ArePluginsReady = true);

			Task WaitForPlugins = Task.Run(async delegate
			{
				Utility.Logger.Debug("Waiting for event OnPluginProcessFinished");
				while (!ArePluginsReady) await Task.Delay(1000);
				return;
			});

			WaitForPlugins.Wait();
			*/
		}
	}
}
