using Autodesk.AutoCAD.EditorInput;
using System;

namespace MeshPlugin
{
    // ШТАМП СБОРКИ.
    //
    // Пока AutoCAD открыт, свежая DLL не может лечь в папку автозагрузки (файл
    // занят), и следующий запуск идёт на ПРЕЖНЕЙ сборке. Внешне это выглядит как
    // «правка не помогла» и не раз уводило отладку в поиск несуществующего бага.
    // Поэтому каждая команда первой строкой печатает, какая именно сборка сейчас
    // работает и откуда она загружена: время файла сравнивается со временем
    // последней сборки, папка показывает, это bundle или NETLOAD из bin.
    //
    // Время берётся из ФАЙЛА, а не из заголовка сборки: проект собирается
    // детерминированно (<Deterministic>true</Deterministic>), и штамп внутри PE —
    // хеш содержимого, а не дата.
    internal static class BuildInfo
    {
        private static string stamp;

        public static string Stamp
        {
            get
            {
                if (stamp != null) return stamp;
                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    string path = asm.Location;
                    string ver = asm.GetName().Version.ToString();
                    DateTime built = System.IO.File.GetLastWriteTime(path);
                    stamp = $"MeshPlugin {ver}, сборка {built:dd.MM.yyyy HH:mm}, папка {System.IO.Path.GetDirectoryName(path)}";
                }
                catch (System.Exception ex)
                {
                    stamp = "MeshPlugin (сборку определить не удалось: " + ex.Message + ")";
                }
                return stamp;
            }
        }
    }

    public partial class Commands
    {
        // Первая строка любой команды MESH*: что запущено и на какой сборке.
        private void EchoCommandStart(Editor ed, string command)
        {
            ed.WriteMessage($"\n{command} — {BuildInfo.Stamp}\n");
        }
    }
}
