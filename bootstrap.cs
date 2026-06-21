using V12.Core;
using V12.Core.Core.Interfaces;
using V12.Basic;
using V12.Basic.Components;
using V12.Components;
using V12.WorldML;
namespace V12.SampleGame
{
    public class Bootstrap : IGameService
    {
        GameRoot _gameroot;
        
        public Bootstrap() { }
        public void Initialize(GameRoot g)
        {
            _gameroot = g;

            Console.WriteLine("Bootstrap.Initialize called");
            V12.Basic.BasicRegistry.RegisterAll(_gameroot);

            var player = new Element { Name= "Player" };
            player.AddComponent(new PlayerComponent());

            bool added = _gameroot.SelectedWorld != null;
            _gameroot.SelectedWorld?.AddElement(player);
            _gameroot.SelectedWorld?.AddElement(Procedurals.GenBox("Box", new System.Numerics.Vector3(1, 1, 1)));
            Console.WriteLine($"Bootstrap: SelectedWorld is null? {!added}");
            try
            {
                var parsedWorld = new WorldMLParser().Parse("""
               

                <World name="Prototype">
                	<Element name="WorldEnvironment">
                		<EnvironmentComponent mode="SolidColor" skyR="0.500" skyG="0.600" skyB="0.800" ambientR="0.000" ambientG="0.000" ambientB="0.000" ambientEnergy="1.000" skyboxPath="" />
                	</Element>
                	<Element name="MeshInstance3D">
                		<TransformComponent x="4.000" y="2.000" z="0.000" rotation="0.000" rotationX="0.000" rotationY="0.000" rotationZ="0.000" />
                		<Component type="MeshComponent" name="mesh_data" Shape="Box" Width="1.000" Height="1.000" Depth="1.000" />
                		<Component type="MeshRenderer" Mesh="mesh_data" />
                		<ColliderComponent Shape="Box" Width="1.000" Height="1.000" Depth="1.000" isTrigger="true" />
                	</Element>
                	<Element name="CSGRoot">
                		<TransformComponent x="-4.000" y="0.000" z="-4.000" rotation="0.000" rotationX="-0.000" rotationY="0.000" rotationZ="0.000" />
                		<Element name="BackWall">
                			<TransformComponent x="-3.946" y="1.000" z="-7.932" rotation="0.000" rotationX="-0.000" rotationY="0.000" rotationZ="0.000" />
                			<Component type="MeshComponent" name="csg_mesh" Shape="Box" Width="6.100" Height="2.000" Depth="0.126" />
                			<Component type="MeshRenderer" Mesh="csg_mesh" />
                			<ColliderComponent Shape="Box" Width="6.100" Height="2.000" Depth="0.126" isTrigger="false" />
                		</Element>
                		<Element name="Roof">
                			<TransformComponent x="-3.946" y="2.100" z="-3.995" rotation="0.000" rotationX="-0.000" rotationY="0.000" rotationZ="0.000" />
                			<Component type="MeshComponent" name="csg_mesh" Shape="Box" Width="6.100" Height="0.200" Depth="8.000" />
                			<Component type="MeshRenderer" Mesh="csg_mesh" />
                			<ColliderComponent Shape="Box" Width="6.100" Height="0.200" Depth="8.000" isTrigger="false" />
                		</Element>
                		<Element name="FrontWall">
                			<TransformComponent x="-3.996" y="1.000" z="-0.132" rotation="0.000" rotationX="-0.000" rotationY="0.000" rotationZ="0.000" />
                			<Component type="MeshComponent" name="csg_mesh" Shape="Box" Width="6.000" Height="2.000" Depth="0.126" />
                			<Component type="MeshRenderer" Mesh="csg_mesh" />
                			<ColliderComponent Shape="Box" Width="6.000" Height="2.000" Depth="0.126" isTrigger="false" />
                			<Element name="Door">
                				<TransformComponent x="-4.100" y="0.800" z="-0.132" rotation="0.000" rotationX="-0.000" rotationY="0.000" rotationZ="0.000" />
                				<Component type="MeshComponent" name="csg_mesh" Shape="Box" Width="1.700" Height="1.600" Depth="0.126" />
                				<Component type="MeshRenderer" Mesh="csg_mesh" />
                				<ColliderComponent Shape="Box" Width="1.700" Height="1.600" Depth="0.126" isTrigger="false" />
                			</Element>
                		</Element>
                		<Element name="RightWall">
                			<TransformComponent x="-0.959" y="1.000" z="-3.938" rotation="-90.000" rotationX="-0.000" rotationY="-90.000" rotationZ="0.000" />
                			<Component type="MeshComponent" name="csg_mesh" Shape="Box" Width="7.864" Height="2.000" Depth="0.126" />
                			<Component type="MeshRenderer" Mesh="csg_mesh" />
                			<ColliderComponent Shape="Box" Width="7.864" Height="2.000" Depth="0.126" isTrigger="false" />
                		</Element>
                		<Element name="CSGBox3D4">
                			<TransformComponent x="-6.959" y="1.000" z="-3.920" rotation="-90.000" rotationX="-0.000" rotationY="-90.000" rotationZ="0.000" />
                			<Component type="MeshComponent" name="csg_mesh" Shape="Box" Width="7.900" Height="2.000" Depth="0.126" />
                			<Component type="MeshRenderer" Mesh="csg_mesh" />
                			<ColliderComponent Shape="Box" Width="7.900" Height="2.000" Depth="0.126" isTrigger="false" />
                		</Element>
                	</Element>
                	<Element name="Node3D">
                		<TransformComponent x="0.000" y="0.900" z="-1.600" rotation="0.000" rotationX="-0.000" rotationY="0.000" rotationZ="0.000" />
                		<Component type="SvgComponent" width="1.000" height="1.000" tintR="1.000" tintG="1.000" tintB="1.000" tintA="1.000">
                			<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" fill="none" viewBox="0 0 16 16"><path fill="#fc7f7f" d="M8 1a5 5 0 0 1 5 5v9H3V6a5 5 0 0 1 5-5m3 7a1 1 0 1 0 0 2 1 1 0 0 0 0-2"/></svg>
                		</Component>
                	</Element>
                	<Element name="CSGBox3D">
                		<TransformComponent x="-4.000" y="-0.250" z="-4.050" rotation="0.000" rotationX="-0.000" rotationY="0.000" rotationZ="0.000" />
                		<Component type="MeshComponent" name="csg_mesh" Shape="Box" Width="24.000" Height="0.500" Depth="23.700" />
                		<Component type="MeshRenderer" Mesh="csg_mesh" />
                	</Element>
                	<Element name="DirectionalLight3D">
                		<TransformComponent x="0.000" y="0.000" z="0.000" rotation="-45.000" rotationX="-45.000" rotationY="-45.000" rotationZ="0.000" />
                		<GenericLightComponent colorR="1.000" colorG="1.000" colorB="1.000" energy="0.500" shadowEnabled="true" />
                	</Element>
                </World>
                
                
                
                
                """);
                Console.WriteLine($"[Bootstrap] Parsed world '{parsedWorld.Name}' with {(parsedWorld.Children?.Count ?? 0)} children");
                if (parsedWorld.Children != null)
                {
                    foreach (var child in parsedWorld.Children)
                    {
                        Console.WriteLine($"[Bootstrap]   Child: '{child.Name}' ({child.Components?.Count ?? 0} components, {child.Children?.Count ?? 0} children)");
                    }
                }
                _gameroot.SelectedWorld!.AddElement(parsedWorld);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Bootstrap] ERROR parsing WorldML: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"[Bootstrap] StackTrace: {ex.StackTrace}");
            }
        }

        public void Update(float deltaTime)
        {
          
        }

        public void Update(GameRoot gameRoot)
        {
            _gameroot = gameRoot;
        }
    }
}
