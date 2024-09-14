using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using API.Assembly;
using Carbon;
using Carbon.Components;
using Carbon.Extensions;
using Carbon.Profiler;
using Utility;
using Logger = Utility.Logger;

namespace Loaders;

internal sealed class AssemblyLoader : IDisposable
{
	private class Item : IAssemblyCache
	{
		public string Name { get; internal set; }
		public byte[] Raw { get; internal set; }
		public Assembly Assembly { get; internal set; }
	}

	private readonly Dictionary<string, Item> _cache = new();

	private readonly string[] _directoryList =
	{
		Context.CarbonManaged,
		Context.CarbonHooks,
		Context.CarbonExtensions,
	};

	internal byte[] _checksumBuffer = new byte[20];
	internal IReadOnlyList<byte> _needleBuffer = [0x01, 0xdc, 0x7f, 0x01];

	internal void ResetChechsum()
	{
		for (int i = 0; i < _checksumBuffer.Length; i++)
		{
			_checksumBuffer[i] = default;
		}
	}

	internal IAssemblyCache Load(string file, string requester,
		string[] directories, IReadOnlyList<string> blackList, IReadOnlyList<string> whiteList, IExtensionManager.ExtensionTypes extensionType = IExtensionManager.ExtensionTypes.Default)
	{
		// normalize filename
		file = Path.GetFileName(file);

		Logger.Debug($"Loading assembly '{file}' requested by '{requester}'");

		string path = default;
		foreach (string directory in (directories is null) ? _directoryList : directories)
		{
			if (!File.Exists(Path.Combine(directory, file))) continue;
			path = Path.Combine(directory, file);
		}

		if (String.IsNullOrEmpty(path))
		{
			Logger.Debug($"Unable to load assembly: '{file}'");
			return default;
		}

		if (blackList is not null || whiteList is not null)
		{
			using Sandbox<AssemblyValidator> sandbox = new Sandbox<AssemblyValidator>();
			sandbox.Proxy.Blacklist = blackList;
			sandbox.Proxy.Whitelist = whiteList;

			if (!sandbox.Proxy.Validate(path))
			{
				Logger.Warn($" >> Validation failed for '{file}'");
				return default;
			}
		}

		byte[] raw = File.ReadAllBytes(path);
		bool converted = false;

		switch (extensionType)
		{
			case IExtensionManager.ExtensionTypes.Extension:
				ConversionResult oxideConvert = Community.Runtime.Compat.AttemptOxideConvert(ref raw);

				switch (oxideConvert)
				{
					case ConversionResult.Fail:
						Logger.Warn($" >> Failed Oxide extension conversion for '{file}'");
						return default;

					default:
						converted = true;
						break;
				}
				break;

			case IExtensionManager.ExtensionTypes.HarmonyMod:
			case IExtensionManager.ExtensionTypes.HarmonyModHotload:
				converted = Community.Runtime.Compat.ConvertHarmonyMod(ref raw);

				if (raw == null)
				{
					Logger.Warn($" >> Failed HarmonyMod conversion for '{file}'");
					return default;
				}
				break;
		}

		string sha1 = Util.sha1(raw);

		if (_cache.TryGetValue(sha1, out Item cache))
		{
			Logger.Debug($"Loaded assembly from cache: "
				+ $"'{cache.Assembly.GetName().Name}' v{cache.Assembly.GetName().Version}");
			return cache;
		}

		Assembly result;

		if (IndexOf(raw, _needleBuffer) == 0)
		{
			ResetChechsum();
			Buffer.BlockCopy(raw, 4, _checksumBuffer, 0, 20);
			result = Assembly.Load(Package(_checksumBuffer, raw, 24));
		}
		else
		{
			result = Assembly.Load(raw);
		}

		switch (extensionType)
		{
			case IExtensionManager.ExtensionTypes.HarmonyMod:
			case IExtensionManager.ExtensionTypes.HarmonyModHotload:
			{
				var fileName = Path.GetFileNameWithoutExtension(file);
				var isProfiled = MonoProfiler.TryStartProfileFor(MonoProfilerConfig.ProfileTypes.Harmony, result, Path.GetFileNameWithoutExtension(file), true);
				Assemblies.Harmony.Update(fileName, result, file, isProfiled);

				if (!converted)
				{
					var hooks = new List<object>();
					var patchCount = Harmony.PatchAll(result, fileName);

					foreach (var type in result.GetTypes())
					{
						if (type.GetInterfaces().All(x => x.Name != "IHarmonyModHooks"))
						{
							continue;
						}

						try
						{
							var mod = Activator.CreateInstance(type);

							if (mod == null)
							{
								Logger.Error($"Failed to create hook instance: Is null ({path} -> {requester})");
							}
							else
							{
								hooks.Add(mod);
							}

							if (extensionType == IExtensionManager.ExtensionTypes.HarmonyModHotload)
							{
								try
								{
									type.GetMethod("OnLoaded").Invoke(mod, new object[1]);
								}
								catch (Exception ex)
								{
									Logger.Error($"Failed to create hook instance ({path} -> {requester})", ex);
								}
							}
						}
						catch (Exception ex)
						{
							Logger.Error($"Failed to create hook instance ({path} -> {requester})", ex);
						}
					}

					Logger.Log($"Loaded '{Path.GetFileNameWithoutExtension(path)}' HarmonyMod with {patchCount:n0} {patchCount.Plural("patch", "patches")}");
					Harmony.ModHooks.Add(result, hooks);
				}

				break;
			}
			

			case IExtensionManager.ExtensionTypes.Extension:
			{
				var isProfiled = MonoProfiler.TryStartProfileFor(MonoProfilerConfig.ProfileTypes.Extension, result, Path.GetFileNameWithoutExtension(file));
				Assemblies.Extensions.Update(Path.GetFileNameWithoutExtension(file), result, file);
				break;
			}
		}

		cache = new Item { Name = file, Raw = raw, Assembly = result };
		_cache.Add(sha1, cache);

		Logger.Debug($"Loaded assembly: '{result.GetName().Name}' v{result.GetName().Version}");
		return cache;
	}

	internal IAssemblyCache ReadFromCache(string name)
	{
		return _cache.Select(x => x.Value).LastOrDefault(x => x.Name == name);
	}

	internal static byte[] Package(IReadOnlyList<byte> a, IReadOnlyList<byte> b, int c = 0)
	{
		var buffer = new byte[b.Count - c];

		for (int i = c; i < b.Count; i++)
		{
			buffer[i - c] = (byte)(b[i] ^ a[(i - c) % a.Count]);
		}

		return buffer;
	}

	internal static int IndexOf(IReadOnlyList<byte> haystack, IReadOnlyList<byte> needle)
	{
		int len = needle.Count;
		int limit = haystack.Count - len;

		for (int i = 0; i <= limit; i++)
		{
			int k = 0;
			for (; k < len; k++)
			{
				if (needle[k] != haystack[i + k])
				{
					break;
				}
			}

			if (k == len) return i;
		}
		return -1;
	}

	private bool _disposing;

	private void Dispose(bool disposing)
	{
		if (!_disposing)
		{
			if (disposing)
				_cache.Clear();
			_disposing = true;
		}
	}

	public void Dispose()
	{
		Dispose(disposing: true);
		GC.SuppressFinalize(this);
	}
}
