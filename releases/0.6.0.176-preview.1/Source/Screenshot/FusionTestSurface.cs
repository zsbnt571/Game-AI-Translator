using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ScreenshotTranslationUiTester;

// Explicit verification surface, never shown during normal startup.
internal sealed class FusionTestSurface : Form
{
    private int _scene;
    private Bitmap _image;
    internal FusionTestSurface()
    {
        Text="Fusion verification surface · F1–F6";ClientSize=new(1000,620);StartPosition=FormStartPosition.CenterScreen;
        KeyPreview=true;DoubleBuffered=true;_image=CreateScene(0);
        KeyDown+=(_,e)=>{if(e.KeyCode>=Keys.F1&&e.KeyCode<=Keys.F6){_scene=e.KeyCode-Keys.F1;_image.Dispose();_image=CreateScene(_scene);Invalidate();}};
        FormClosed+=(_,_)=>_image.Dispose();
    }
    protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);e.Graphics.DrawImage(_image,ClientRectangle);}
    internal static void Export(string root)
    {Directory.CreateDirectory(root);for(int i=0;i<6;i++){using var image=CreateScene(i);image.Save(Path.Combine(root,$"scene-{i+1}.png"),ImageFormat.Png);}}
    internal static Bitmap CreateScene(int scene)
    {
        var image=new Bitmap(1000,620);using var g=Graphics.FromImage(image);g.SmoothingMode=SmoothingMode.AntiAlias;
        using var bg=new LinearGradientBrush(new Rectangle(0,0,1000,620),Color.FromArgb(29,46,64),Color.FromArgb(12,21,33),35);g.FillRectangle(bg,0,0,1000,620);
        using var accent=new SolidBrush(Color.FromArgb(240,197,113));using var ink=new SolidBrush(Color.FromArgb(244,244,236));
        void Text(string text,float x,float y,float size=26,bool title=false)
        {using var font=new Font("Segoe UI",size,title?FontStyle.Bold:FontStyle.Regular,GraphicsUnit.Pixel);g.DrawString(text,font,title?accent:ink,x,y);}
        void Panel(int x,int y,int w,int h){using var b=new SolidBrush(Color.FromArgb(190,8,17,27));g.FillRectangle(b,x,y,w,h);}
        switch(scene)
        {
            case 0:Text("THE QUIET SHORE",50,45,32,true);Panel(50,325,900,235);Text("MARA",80,344,20,true);Text("We have been here for two weeks.",80,390);Text("The old bridge is six feet above the water.",80,438);Text("Come back before the sun sets.",80,486);break;
            case 1:Text("A NEW JOURNEY",90,90,46,true);Text("Continue",105,235,30);Text("New Game",105,300,30);Text("Settings",105,365,30);Text("Return to the village",105,470,23);break;
            case 2:Text("FIELD NOTES",50,40,34,true);Panel(50,125,425,390);Panel(515,125,430,390);Text("HERBAL TONIC",75,154,24,true);Text("Restores health over time.",75,212,20);Text("Quantity  x1",75,292,22);Text("Recovery  20%",75,345,22);Text("TRAVEL PACK",540,154,24,true);Text("Carry only what you need.",540,212,20);Text("Slots  3/30",540,292,22);Text("Weight  2 kg",540,345,22);break;
            case 3:for(int i=0;i<14;i++){using var stripe=new Pen(Color.FromArgb(45,86+i*7,130,146),20);g.DrawLine(stripe,i*90,0,i*90-350,620);}Panel(75,300,850,225);Text("A LETTER FROM HOME",98,315,25,true);Text("I will return in 2 weeks.",98,370,30);Text("Keep the lantern burning for me.",98,428,28);break;
            case 4:Text("SUPPLY CHECK",55,50,34,true);Text("x1     ×1     20%     3/30",70,150,32);Text("The first trip takes 2 weeks.",70,255,28);Text("We crossed the river twice.",70,328,28);Text("There are six feet of snow outside.",70,401,28);Text("Bring two warm blankets.",70,474,28);break;
            default:Text("AT THE GATE",55,45,32,true);Panel(50,140,895,160);Panel(50,335,895,180);Text("GUARD",75,155,20,true);Text("Do you have the key, {player_name}?",75,204,26);Text("TRAVELER",75,350,20,true);Text("Yes. I found it near the old tower.",75,405,26);Text("Please let us through.",75,451,26);break;
        }
        return image;
    }
}
