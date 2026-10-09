using System;
using V12.Basic;
using V12.Monogame;
using V12.SampleGame;

namespace V12.Monogame.DX
{
	public static class Program
	{
		[STAThread]
		static void Main()
		{
			MonogameLauncher.Run(root =>
			{
				// Systems must be registered before GameRoot.Initialize() runs the
				// service-init loop; Bootstrap would otherwise register them too late.
				BasicRegistry.RegisterAll(root);
				root.Registry.Register("Bootstrap", new Bootstrap());
			});
		}
	}
}
