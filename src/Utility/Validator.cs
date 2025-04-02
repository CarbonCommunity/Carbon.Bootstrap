using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Facepunch;
using Facepunch.Extend;
using Mono.Cecil;

namespace Utility;

public sealed class AssemblyValidator : Pool.IPooled
{
	public List<string> blacklist;
	public List<string> whitelist;

	public bool Validate(string file)
	{
		// no blacklist, no whitelist, no validation
		if (blacklist == null && whitelist == null) return true;

		try
		{
			byte[] raw = File.ReadAllBytes(file)
			?? throw new Exception($"Unable to read '{file}' from disk");

			using MemoryStream input = new MemoryStream(raw);
			AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(
				input, parameters: new ReaderParameters { InMemory = true });

			foreach (ModuleDefinition module in assembly.Modules)
			{
				foreach (AssemblyNameReference reference in module.AssemblyReferences)
				{
					if (blacklist is not null)
					{
						foreach (string expr in blacklist)
						{
							if (Regex.IsMatch(reference.Name, expr))
								throw new Exception($" >> Reference '{reference.Name}' not allowed by blacklisting");
						}
					}

					if (whitelist is not null)
					{
						if (!whitelist.Contains(reference.Name))
							throw new Exception($" >> Reference '{reference.Name}' not allowed by whitelisting");
					}
				}
			}
		}
		catch (System.Exception e)
		{
			Logger.Warn(e.Message);
			return false;
		}
		return true;
	}

	public void EnterPool()
	{
		if (blacklist != null)
		{
			Pool.FreeUnmanaged(ref blacklist);
		}
		if (whitelist != null)
		{
			Pool.FreeUnmanaged(ref whitelist);
		}
	}

	public void LeavePool()
	{
		blacklist = Pool.Get<List<string>>();
		whitelist = Pool.Get<List<string>>();
	}
}
