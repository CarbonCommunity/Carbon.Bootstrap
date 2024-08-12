using System;
using System.Collections;
using Carbon;
using Carbon.Core;
using HarmonyLib;

namespace Patches;

[HarmonyPatch(typeof(FileSystem_Warmup), nameof(FileSystem_Warmup.Run), new System.Type[] { typeof(string[]), typeof(Action<string>), typeof(string), typeof(int) })]
internal static class FileSystem_WarmupHalt
{
	internal static bool IsReady = false;
	internal static bool AllowNative = false;

	public static IEnumerator Process(string[] assetList, Action<string> statusFunction = null, string format = null, int priority = 0)
	{
		while (!IsReady || !ModLoader.IsBatchComplete)
		{
			yield return null;
		}

		AllowNative = true;

		yield return FileSystem_Warmup.Run(assetList, statusFunction, format, priority);
	}

	public static bool Prefix(string[] assetList, Action<string> statusFunction, string format, int priority, ref IEnumerator __result)
	{
		if (AllowNative || (IsReady && ModLoader.IsBatchComplete))
		{
			return true;
		}

		__result = Process(assetList, statusFunction, format, priority);
		return false;
	}
}
