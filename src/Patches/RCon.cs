﻿using Facepunch;
using HarmonyLib;

/*
 *
 * Copyright (c) 2022-2024 Carbon Community
 * All rights reserved.
 *
 */

namespace Patches;
#pragma warning disable IDE0051

internal static class __RCon
{
	[HarmonyPatch(typeof(RCon), methodName: nameof(RCon.OnCommand))]
	internal static class __OnCommand
	{
		[HarmonyPriority(int.MaxValue)]
		private static bool Prefix(RCon.Command cmd)
		{
			switch (cmd.Name)
			{
				case "c.boot":
					// if (!HarmonyLoaderEx.GetInstance().IsLoaded("Carbon.dll"))
					// 	HarmonyLoaderEx.GetInstance().Load("Carbon.dll");
					return false;

				default:
					return true;
			}
		}
	}
}
