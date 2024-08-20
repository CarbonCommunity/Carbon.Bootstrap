using System;
using System.Diagnostics;
using Facepunch;

namespace Utility;

public struct TimeMeasure : IDisposable
{
	private Stopwatch _watch;
	private string _name;

	public static TimeMeasure New(string name)
	{
		TimeMeasure result = default(TimeMeasure);
		result._watch = Pool.Get<Stopwatch>();
		result._name = name;

		result._watch.Start();
		return result;
	}

	public void Dispose()
	{
		Logger.Debug($"[PROFILER] {_name} took {_watch.ElapsedMilliseconds:0}ms");
		_watch.Reset();
		Pool.FreeUnsafe(ref _watch);
	}
}
