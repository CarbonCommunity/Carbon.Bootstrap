using System;
using System.Collections;
using System.Collections.Generic;
using API.Events;
using Carbon;
using HarmonyLib;
using UnityEngine;

/*
 *
 * Copyright (c) 2022-2024 Carbon Community
 * All rights reserved.
 *
 */

namespace Patches;

internal static class __BootstrapHalt
{
	[HarmonyPatch(typeof(Bootstrap), nameof(Bootstrap.StartServer), new System.Type[] { typeof(bool), typeof(string), typeof(bool) })]
	internal static class IBootstrapHalt
	{
		internal static bool AllowNative = false;

		public static IEnumerator Process(bool doLoad, string saveFileOverride, bool allowOutOfDateSaves)
		{
			while (!Community.AllProcessorsFinalized)
			{
				yield return null;
			}

			AllowNative = true;

			yield return Bootstrap.StartServer(doLoad, saveFileOverride, allowOutOfDateSaves);
		}

		public static bool Prefix(bool doLoad, string saveFileOverride, bool allowOutOfDateSaves, ref IEnumerator __result)
		{
			if (AllowNative || Community.AllProcessorsFinalized)
			{
				return true;
			}

			__result = Process(doLoad, saveFileOverride, allowOutOfDateSaves);
			return false;
		}
	}
}
