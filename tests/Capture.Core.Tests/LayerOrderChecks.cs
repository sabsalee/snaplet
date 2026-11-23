using Capture.Core;

/// <summary>The object list is the drawing order: later objects sit on top, and the layer panel reorders it.</summary>
internal static class LayerOrderChecks
{
    internal static void Run(Action<bool, string> check)
    {
        Annotation Make(AnnotationKind kind) => new(Guid.NewGuid(), kind, new(1, 2), new(30, 40), new(255, 0, 0),
            Image: kind == AnnotationKind.Image ? new RasterImage(1, 1, [0, 0, 0, 255]) : null);
        Guid[] Order(EditableDocument d) => d.Annotations.Select(a => a.Id).ToArray();

        var document = new EditableDocument(100, 80);
        var spotlight = Make(AnnotationKind.Spotlight); var text = Make(AnnotationKind.Text); var arrow = Make(AnnotationKind.Arrow);
        document.Add(spotlight); document.Add(text); document.Add(arrow);
        check(Order(document).SequenceEqual([spotlight.Id, text.Id, arrow.Id]), "Objects stack in the order they were made.");

        var picture = Make(AnnotationKind.Image);
        document.Add(picture);
        check(document.Annotations[0].Id == picture.Id, "An added image goes under the marks.");
        var second = Make(AnnotationKind.Image);
        document.Add(second);
        check(Order(document).Take(2).SequenceEqual([picture.Id, second.Id]), "A second image goes above the first image but under the marks.");

        int changes = 0; document.Changed += (_, _) => changes++;
        document.Reorder(text.Id, document.Annotations.Count - 1);
        check(document.Annotations[^1].Id == text.Id && changes == 1, "Reorder brings an object to the front in one change.");
        document.Undo();
        check(Order(document).SequenceEqual([picture.Id, second.Id, spotlight.Id, text.Id, arrow.Id]), "Undo restores the previous order.");
        document.Redo();
        check(document.Annotations[^1].Id == text.Id, "Redo applies the reorder again.");

        changes = 0;
        document.Reorder(text.Id, 99);
        document.Reorder(Guid.NewGuid(), 0);
        check(changes == 0 && document.Annotations[^1].Id == text.Id, "Reordering to the same place or a missing object changes nothing.");
        document.Reorder(text.Id, -5);
        check(document.Annotations[0].Id == text.Id, "An index below zero sends the object to the back.");
        check(document.Annotations.Count == 5, "Reordering never adds or drops objects.");
    }
}
