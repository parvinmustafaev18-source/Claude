"""Build an isolated harness from the current C# source; no AutoCAD runtime emulation."""
from pathlib import Path
import argparse
parser = argparse.ArgumentParser()
parser.add_argument('--output', type=Path, default=Path('/tmp/meshplugin-thick-tests'))
args = parser.parse_args()
repo = Path(__file__).resolve().parents[2]
out = args.output.resolve()
out.mkdir(parents=True, exist_ok=True)
def member(file,marker):
 s=(repo/file).read_text(encoding='utf-8-sig');a=s.index(marker);b=s.index('{',a);depth=1;i=b+1
 while depth:
  if s[i]=='{':depth+=1
  if s[i]=='}':depth-=1
  i+=1
 return s[a:i]
methods=[member('ThickZone.cs','        internal List<Point2d[]> SplitMeshAtThickBoundaries(')]
methods.append(member('ThickZone.cs','        internal bool HasIsolatedThickBoundary('))
methods.append(member('SelfCheck.cs','        private bool IsOnPolygonBoundary('))
methods.append(member('QuadMesh.cs','        private List<Point2d[]> SplitSegmentsAtNodes('))
methods.append(member('QuadMesh.cs','        private bool Emit('))

for file,marker in [
 ('QuadMesh.cs','        private List<Point2d[]> ClipSegmentsOutsideColumns('),
 ('QuadMesh.cs','        private List<Point2d[]> DeduplicateSegments('),
 ('QuadMesh.cs','        private List<Point2d[]> SplitSegmentsAtPoints('),
 ('SelfCheck.cs','        private double ElementPlanArea('),
 ('SelfCheck.cs','        private double SlabTargetArea('),
 ('SelfCheck.cs','        private List<string> AreaBalanceLines(')]:methods.append(member(file,marker))
for name in ('ProblemLayerName', 'HoleLayerName'):
 methods.append(next(line for line in (repo/'Defs.cs').read_text().splitlines()
                     if 'private const string '+name+' =' in line))
source='using System;\nusing System.Collections.Generic;\nusing Autodesk.AutoCAD.Geometry;\nnamespace MeshPlugin { public partial class Commands {\n'+'\n'.join(methods)+'''
public List<Point2d[]> ClipBoundary(List<Point2d[]> edges, List<List<Point2d>> voids) {return ClipSegmentsOutsideColumns(edges,voids,out _,out _);}
public double TotalFaceArea(List<Point2d[]> segments, bool onlyThick = false) {
 var input=new ExportInput { Contour=new List<Point2d>{new Point2d(0,0),new Point2d(10,0),new Point2d(10,10),new Point2d(0,10)},Segments=segments };
 input.ThickZones.Add(new ThickZone {ThicknessMm=500, Poly=new List<Point2d>{new Point2d(2,2),new Point2d(8,2),new Point2d(8,8),new Point2d(2,8)}});
 var task=BuildExportCore(input);if(!task.Ok)throw new Exception(task.Error);
 if(task.TaskText.IndexOf("0.5 RO",StringComparison.Ordinal)<0)throw new Exception("GEI thickness missing");
 double sum=0;foreach(var el in task.Elements){
 string title;bool thick=task.StiffTitles.TryGetValue(el[1],out title) && title.IndexOf("плита другой толщины",StringComparison.Ordinal)>=0;
 if(!onlyThick || thick)sum+=ElementPlanArea(el,task.Nodes3);
 }return sum;
}
}}\n'''
(out/'Cut.cs').write_text(source)
(out/'Geometry.cs').write_text((repo/'Geometry.cs').read_text(encoding='utf-8-sig'))
(out/'ExportCore.cs').write_text((repo/'ExportCore.cs').read_text(encoding='utf-8-sig'))
(out/'ThickZoneModel.cs').write_text('using System.Collections.Generic;using Autodesk.AutoCAD.Geometry;namespace MeshPlugin {'+member('ThickZone.cs','    internal class ThickZone')+'}')
(out/'SpatialGrid.cs').write_text((repo/'SpatialGrid.cs').read_text(encoding='utf-8-sig'))
(out/'MeshTol.cs').write_text('using System; namespace MeshPlugin {\n'+member('Defs.cs','    internal static class MeshTol')+'\n}')
(out/'Harness.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><LangVersion>7.3</LangVersion><ImplicitUsings>disable</ImplicitUsings><Nullable>disable</Nullable></PropertyGroup></Project>')
(out/'NuGet.Config').write_text('<configuration><packageSources><clear /></packageSources></configuration>')

(out/'Program.cs').write_text(Path(__file__).with_name('Program.cs').read_text())
print('Prepared C# 7.3 harness in', out)
