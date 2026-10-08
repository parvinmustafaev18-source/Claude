using System;
using System.Collections.Generic;
using System.Linq;
using MeshPlugin;
using Autodesk.AutoCAD.Geometry;
// Coordinate-only stand-in for isolated algorithm tests; does not emulate AutoCAD runtime.
namespace Autodesk.AutoCAD.Geometry {
 public struct Point2d {
  public static Point2d Origin {get {return new Point2d(0,0);}}
  public double X {get;} public double Y {get;}
  public Point2d(double x,double y){X=x;Y=y;}
  public double GetDistanceTo(Point2d p){double dx=X-p.X,dy=Y-p.Y;return Math.Sqrt(dx*dx+dy*dy);}
 }
}
class Program {
 static Point2d P(double x,double y){return new Point2d(x,y);}
 static Point2d[] S(double x,double y,double xx,double yy){return new[]{P(x,y),P(xx,yy)};}
 static List<Point2d[]> L(params Point2d[][] s){return s.ToList();}
 static void Assert(bool ok,string reason){if(!ok)throw new Exception(reason);}
 static string Key(Point2d[] s){string a=s[0].X.ToString("F6")+","+s[0].Y.ToString("F6"),b=s[1].X.ToString("F6")+","+s[1].Y.ToString("F6");return string.CompareOrdinal(a,b)<0?a+";"+b:b+";"+a;}
 static HashSet<string> Keys(List<Point2d[]> s){return new HashSet<string>(s.Select(Key));}
 static int count;
 static void Test(string name,Action action){action();count++;Console.WriteLine("PASS "+name);}
 static void Main(){
 var c=new Commands();int cut,add;
 Test("crossing creates four pieces with common point",()=>{
 var r=c.SplitMeshAtThickBoundaries(L(S(0,0,10,0)),L(S(5,-5,5,5)),out cut,out add);
 Assert(cut==1 && add==2 && r.Count==4,"counts");Assert(r.All(s=>s[0].GetDistanceTo(P(5,0))==0||s[1].GetDistanceTo(P(5,0))==0),"shared node");});
 Test("intersection 0.2 mm from endpoint is preserved",()=>{
 var r=c.SplitMeshAtThickBoundaries(L(S(0,0,10,0)),L(S(.2,-5,.2,5)),out cut,out add);Assert(cut==1 && r.Count==4,"near endpoint lost");Assert(Keys(r).Contains(Key(S(0,0,.2,0))),"short piece lost");});
 Test("T junction cuts old line",()=>{var r=c.SplitMeshAtThickBoundaries(L(S(0,0,10,0)),L(S(5,0,5,5)),out cut,out add);Assert(cut==1 && add==1 && r.Count==3,"T junction");});
 Test("existing endpoint cuts contour",()=>{var r=c.SplitMeshAtThickBoundaries(L(S(5,-5,5,0)),L(S(0,0,10,0)),out cut,out add);Assert(cut==0 && add==2 && r.Count==3,"reverse T");});
 Test("collinear overlap is split without duplicates",()=>{var r=c.SplitMeshAtThickBoundaries(L(S(0,0,10,0)),L(S(3,0,7,0)),out cut,out add);Assert(cut==1 && add==0 && r.Count==3,"overlap");Assert(Keys(r).Count==3,"duplicate");});
 Test("repeated cut changes nothing",()=>{var b=L(S(5,-5,5,5));var r=c.SplitMeshAtThickBoundaries(L(S(0,0,10,0)),b,out cut,out add);var rr=c.SplitMeshAtThickBoundaries(r,b,out cut,out add);Assert(cut==0 && add==0 && r.Count==rr.Count && Keys(r).SetEquals(Keys(rr)),"repeat");});
 Test("distant manual edges and duplicates preserved",()=>{var old=S(100,100,110,100);var r=c.SplitMeshAtThickBoundaries(L(S(0,0,10,0),old,old),L(S(5,-5,5,5)),out cut,out add);Assert(r.Count(s=>Key(s)==Key(old))==2,"distant duplicates modified");});
 Test("multiple cuts sorted and keep original endpoints",()=>{var r=c.SplitMeshAtThickBoundaries(L(S(0,0,10,0)),L(S(8,-1,8,1),S(2,-1,2,1)),out cut,out add);Assert(cut==1&&r.Count==7,"multiple");Assert(Keys(r).Contains(Key(S(0,0,2,0)))&&Keys(r).Contains(Key(S(8,0,10,0))),"endpoints");});
 Test("closed cut through cell preserves exported face area",()=>{
 var grid=L(S(0,0,10,0),S(10,0,10,10),S(10,10,0,10),S(0,10,0,0),S(5,0,5,5),S(5,5,5,10),S(0,5,5,5),S(5,5,10,5));
 var b=L(S(2,2,8,2),S(8,2,8,8),S(8,8,2,8),S(2,8,2,2));var r=c.SplitMeshAtThickBoundaries(grid,b,out cut,out add);
 Assert(Math.Abs(c.TotalFaceArea(r)-100)<1e-8,"area changed");Assert(cut==4,"four original grid edges split");Assert(Math.Abs(c.TotalFaceArea(r,true)-36)<1e-8,"thick area");Assert(!c.HasIsolatedThickBoundary(r,add,new List<List<Point2d>>()),"connected cut rejected");});
 Test("cut splits triangular cells and preserves thick area",()=>{
 var mesh=L(S(0,0,10,0),S(10,0,10,10),S(10,10,0,10),S(0,10,0,0),S(0,0,10,10));
 var b=L(S(2,2,8,2),S(8,2,8,8),S(8,8,2,8),S(2,8,2,2));var r=c.SplitMeshAtThickBoundaries(mesh,b,out cut,out add);
 Assert(Math.Abs(c.TotalFaceArea(r)-100)<1e-8,"triangle total area");Assert(Math.Abs(c.TotalFaceArea(r,true)-36)<1e-8,"triangle thick area");Assert(!c.HasIsolatedThickBoundary(r,add,new List<List<Point2d>>()),"triangle cut rejected");});
 Test("slanted contour preserves exported area",()=>{
 var mesh=L(S(0,0,10,0),S(10,0,10,10),S(10,10,0,10),S(0,10,0,0),S(5,0,5,5),S(5,5,5,10),S(0,5,5,5),S(5,5,10,5));
 var b=L(S(5,2,8,5),S(8,5,5,8),S(5,8,2,5),S(2,5,5,2));var r=c.SplitMeshAtThickBoundaries(mesh,b,out cut,out add);Assert(Math.Abs(c.TotalFaceArea(r)-100)<1e-8,"slanted area");});
 Test("island fully inside cell is rejected",()=>{
 var mesh=L(S(0,0,10,0),S(10,0,10,10),S(10,10,0,10),S(0,10,0,0));
 var b=L(S(2,2,8,2),S(8,2,8,8),S(8,8,2,8),S(2,8,2,2));var r=c.SplitMeshAtThickBoundaries(mesh,b,out cut,out add);Assert(c.HasIsolatedThickBoundary(r,add,new List<List<Point2d>>()),"island accepted");});
 Test("contour connected to slab outline is accepted",()=>{
 var mesh=L(S(5,0,5,10));var b=L(S(2,0,8,0),S(8,0,8,5),S(8,5,2,5),S(2,5,2,0));var r=c.SplitMeshAtThickBoundaries(mesh,b,out cut,out add);
 var outlines=new List<List<Point2d>>{new List<Point2d>{P(0,0),P(10,0),P(10,10),P(0,10)}};
 Assert(!c.HasIsolatedThickBoundary(r,add,outlines),"slab connections ignored");});
 Test("boundary is clipped outside opening",()=>{
 var holes=new List<List<Point2d>>{new List<Point2d>{P(4,4),P(6,4),P(6,6),P(4,6)}};
 var b=c.ClipBoundary(L(S(0,5,10,5)),holes);Assert(b.Count==2,"opening parts");Assert(Keys(b).SetEquals(Keys(L(S(0,5,4,5),S(6,5,10,5)))),"line inside opening");});
 Test("single contact island is rejected",()=>{
 var mesh=L(S(0,0,10,0),S(10,0,10,10),S(10,10,0,10),S(0,10,0,0));
 var b=L(S(0,5,2,3),S(2,3,4,5),S(4,5,2,7),S(2,7,0,5));var r=c.SplitMeshAtThickBoundaries(mesh,b,out cut,out add);Assert(c.HasIsolatedThickBoundary(r,add,new List<List<Point2d>>()),"single contact accepted");});
 Test("negative translated coordinates",()=>{var r=c.SplitMeshAtThickBoundaries(L(S(-10000,-7000,-9990,-7000)),L(S(-9995,-7005,-9995,-6995)),out cut,out add);Assert(cut==1&&add==2,"translation");});
 Test("100 varied intersections preserve length",()=>{var rand=new Random(42);for(int i=0;i<100;i++){double x=.01+rand.NextDouble()*999.98;var r=c.SplitMeshAtThickBoundaries(L(S(0,0,1000,0)),L(S(x,-30,x,30)),out cut,out add);Assert(cut==1&&add==2,"cut count");double len=r.Where(s=>Math.Abs(s[0].Y)<1e-9&&Math.Abs(s[1].Y)<1e-9).Sum(s=>s[0].GetDistanceTo(s[1]));Assert(Math.Abs(len-1000)<1e-8,"mesh length");}});
 Console.WriteLine("Tests passed: "+count);
 }
}
