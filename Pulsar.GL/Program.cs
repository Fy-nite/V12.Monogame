#nullable enable

using System;
using System.IO;
using System.Reflection;
using V12.Basic;
using V12.Core;
using V12.Monogame;
using V12.Pak;

namespace V12.Monogame.GL
{
	public static class Program
	{
		// The pak loader materializes the archive into a temp directory; the
		// resolver points at it, so the result must stay alive for the process.
		private static V12PakLoadResult? _pak;
		private static int _firstPakIndex;

		[STAThread]
		static void Main()
		{
			// Godot-style game executable: rename this host to the game's name and
			// drop <executable name>.v12pak next to it. When that pak exists it is
			// the game; otherwise the host starts with its default empty world.
			MonogameLauncher.Run(
				configureServices: root =>
				{
					// Systems must be registered before GameRoot.Initialize() runs the
					// service-init loop (game hosts can register their own services here).
					BasicRegistry.RegisterAll(root);
					LoadSiblingPak(root);
				},
				afterInitialize: StartLoadedPak);
		}

		private static void LoadSiblingPak(GameRoot root)
		{
			var pakPath = FindSiblingPak();
			if (pakPath == null) return;

			_firstPakIndex = root.Gamepaks.Gamepaks.Count;
			Console.WriteLine($"[Pulsar] Launching '{Path.GetFileName(pakPath)}'...");

			try
			{
				var result = root.LoadPak(pakPath, new V12PakOptions
				{
					LoadDlls = true,
					LoadContractScripts = true,
					LoadCSharpScripts = true,
				});

				_pak = result;
				AppDomain.CurrentDomain.ProcessExit += (_, _) => _pak?.Dispose();

				// Prefer the pak's first world over the host's empty fallback.
				if (result.LoadedWorlds.Count > 0)
					root.SelectWorld(result.LoadedWorlds[0]);
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"[Pulsar] Failed to load '{pakPath}': {ex.Message}");
			}
		}

		private static void StartLoadedPak(GameRoot root)
		{
			if (_pak == null) return;
			root.Gamepaks.InitializeAndStartFrom(_firstPakIndex);
		}

		/// <summary>
		/// Finds <c>&lt;executable name&gt;.v12pak</c> in the executable's own
		/// directory, matching case-insensitively on platforms that allow it.
		/// </summary>
		private static string? FindSiblingPak()
		{
			var exePath = ResolveExecutablePath();
			var directory = exePath != null
				? Path.GetDirectoryName(exePath)
				: AppContext.BaseDirectory;
			if (string.IsNullOrEmpty(directory)) return null;

			var exeName = exePath != null ? Path.GetFileNameWithoutExtension(exePath) : null;
			if (string.IsNullOrEmpty(exeName)) return null;

			var exact = Path.Combine(directory, exeName + ".v12pak");
			if (File.Exists(exact)) return exact;

			foreach (var candidate in Directory.EnumerateFiles(directory, "*.v12pak", SearchOption.TopDirectoryOnly))
			{
				if (string.Equals(Path.GetFileNameWithoutExtension(candidate), exeName, StringComparison.OrdinalIgnoreCase))
					return candidate;
			}

			return null;
		}

		/// <summary>
		/// The host executable path, falling back to the entry assembly when the
		/// process was started through the shared <c>dotnet</c> muxer.
		/// </summary>
		private static string? ResolveExecutablePath()
		{
			var processPath = Environment.ProcessPath;
			if (!string.IsNullOrEmpty(processPath) &&
				!string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
			{
				return processPath;
			}

			return Assembly.GetEntryAssembly()?.Location;
		}
	}
}
