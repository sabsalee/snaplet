using Capture.Core;
namespace Capture.Windows.Editor;

internal sealed record EditorPreset(string Name,ColorRgba Color,double Size,AnnotationStyle Style)
{
    internal Annotation Sample(AnnotationKind kind)=>new(Guid.Parse("a25323c7-bb59-4ef2-aacb-3cb304ef3011"),kind,
        kind==AnnotationKind.Step?new(76,32):new(20,16),kind is AnnotationKind.Arrow or AnnotationKind.Line?new(140,49):new(140,49),Color,Size,
        kind==AnnotationKind.Text?"좋은 아이디어":kind==AnnotationKind.Step?"3":"",Tail:new(60,64),Style:Style,
        Points:kind==AnnotationKind.Pen?[new(20,46),new(48,24),new(78,38),new(110,20),new(140,30)]:null);
}
internal static class EditorPresets
{
    internal static IReadOnlyList<EditorPreset> For(AnnotationKind kind)
    {
        var red=new ColorRgba(239,68,83);var blue=new ColorRgba(59,105,233);var teal=new ColorRgba(15,143,130);var ink=new ColorRgba(38,48,68);
        var basic=new AnnotationStyle(CornerRadius:12,TailWidth:28);
        if(kind==AnnotationKind.Arrow)return [new("가벼운 포인터",red,2.5,basic with {ArrowHead=ArrowHead.Open}),new("리본 강조",blue,3,basic),new("정밀한 선",ink,2,basic with {ArrowHead=ArrowHead.Simple}),new("양방향 비교",teal,2.5,basic with {ArrowHead=ArrowHead.Double})];
        if(kind==AnnotationKind.Text)return [new("코랄 말풍선",red,3,basic),new("블루 말풍선",blue,3,basic with {CornerRadius=16,TailWidth=32}),new("차분한 라벨",ink,3,basic with {ShowTail=false}),new("간결한 텍스트",ink,3,basic with {Bubble=false,ShowTail=false})];
        if(kind==AnnotationKind.Step)return [new("코랄 포인터",red,4,basic),new("블루 포인터",blue,4,basic),new("미니멀 번호",ink,3,basic with {ShowTail=false}),new("민트 번호",teal,4,basic with {ShowTail=false})];
        if(kind==AnnotationKind.Highlight)return [new("옐로 마커",new(250,204,21),4,basic with {MarkerCap=MarkerCap.Round}),new("민트 마커",new(52,211,153),4,basic with {MarkerCap=MarkerCap.Round}),new("라벤더 영역",new(167,139,250),3,basic with {HighlightShape=MarkerShape.Rectangle}),new("자유로운 마커",new(250,204,21),4,basic with {HighlightShape=MarkerShape.Freehand,MarkerCap=MarkerCap.Round})];
        if(kind is AnnotationKind.Pen or AnnotationKind.Line)return [new("선명한 코랄",red,3,basic),new("정밀한 블루",blue,2,basic),new("차분한 잉크",ink,2,basic),new("부드러운 민트",teal,4,basic)];
        return [new("깔끔한 테두리",red,2.5,basic),new("부드러운 채움",blue,2,basic with {Fill=ShapeFill.Translucent,CornerRadius=16}),new("포인터 카드",teal,2,basic with {Fill=ShapeFill.Solid,ShapeTail=true,TailWidth=32}),new("손그림 강조",ink,2,basic with {Sketch=true})];
    }
}
