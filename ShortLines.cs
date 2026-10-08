using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using System;
using System.Collections.Generic;

namespace MeshPlugin
{
    public partial class Commands
    {
        // Redraw сохраняет итоговое выделение после завершения команды.
        [CommandMethod("LIRSHORT", CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void FindShortLinesCommand()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            EchoCommandStart(ed, "LIRSHORT");
            try
            {
                var lengthOptions = new PromptDoubleOptions("\nМаксимальная длина искомых отрезков, мм: ");
                lengthOptions.DefaultValue = MeshTol.MinElementSize;
                lengthOptions.AllowNegative = false;
                lengthOptions.AllowZero = true;
                var lengthResult = ed.GetDouble(lengthOptions);
                if (lengthResult.Status != PromptStatus.OK && lengthResult.Status != PromptStatus.None) return;
                double maximum = lengthResult.Status == PromptStatus.None
                    ? lengthOptions.DefaultValue : lengthResult.Value;
                if (double.IsNaN(maximum) || double.IsInfinity(maximum) || maximum < 0)
                {
                    ed.WriteMessage("\nНужна конечная длина не меньше нуля.\n");
                    return;
                }

                var selectionOptions = new PromptSelectionOptions();
                selectionOptions.MessageForAdding = "\nВыделите область поиска отрезков и нажмите Enter: ";
                selectionOptions.AllowDuplicates = false;
                var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "LINE") });
                var selection = ed.GetSelection(selectionOptions, filter);
                if (selection.Status != PromptStatus.OK) return;

                var found = new List<ObjectId>();
                int checkedLines = 0;
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in selection.Value.GetObjectIds())
                    {
                        var line = tr.GetObject(id, OpenMode.ForRead) as Line;
                        if (line == null) continue;
                        checkedLines++;
                        if (line.Length <= maximum) found.Add(id);
                    }
                }
                // Только итоговое выделение: геометрия, слои и свойства не меняются.
                ed.SetImpliedSelection(found.ToArray());
                ed.WriteMessage($"\nLIRSHORT: проверено отрезков {checkedLines}, выделено {found.Count}, максимальная длина {maximum:g} мм.\n");
                if (found.Count == 0) ed.WriteMessage("В выбранной области отрезков такой длины или короче нет.\n");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nОшибка LIRSHORT: {ex.Message}\nЧертёж не изменён.\n");
            }
        }
    }
}
