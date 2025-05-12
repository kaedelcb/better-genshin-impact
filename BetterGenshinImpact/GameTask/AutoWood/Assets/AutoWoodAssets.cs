using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.GameTask.Model;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoWood.Assets;

public class AutoWoodAssets : BaseAssets<AutoWoodAssets>
{
    public RecognitionObject TheBoonOfTheElderTreeRo;

    // public RecognitionObject CharacterGuideRo;
    public RecognitionObject MenuBagRo;

    public RecognitionObject ConfirmRo;
    public RecognitionObject EnterGameRo;
    public RecognitionObject ExitSwitchRo;
    public RecognitionObject ExitPhoneRo;

    // 木头数量
    public Rect WoodCountUpperRect;

    private AutoWoodAssets()
    {

        WoodCountUpperRect = new Rect((int)(100 * AssetScale), (int)(450 * AssetScale), (int)(300 * AssetScale), (int)(250 * AssetScale));

        //「王树瑞佑」
        TheBoonOfTheElderTreeRo = new RecognitionObject
        {
            Name = "TheBoonOfTheElderTree",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("AutoWood", "TheBoonOfTheElderTree.png"),
            RegionOfInterest = new Rect(CaptureRect.Width - CaptureRect.Width / 4, CaptureRect.Height / 2,
                CaptureRect.Width / 4, CaptureRect.Height - CaptureRect.Height / 2),
            DrawOnWindow = false
        }.InitTemplate();

        // CharacterGuideRo = new RecognitionObject
        // {
        //     Name = "CharacterGuide",
        //     RecognitionType = RecognitionTypes.TemplateMatch,
        //     TemplateImageMat = GameTaskManager.LoadAssetImage("AutoWood", "character_guide.png"),
        //     RegionOfInterest = new Rect(0, 0, CaptureRect.Width / 2, CaptureRect.Height),
        //     DrawOnWindow = false
        // }.InitTemplate();

        MenuBagRo = new RecognitionObject
        {
            Name = "MenuBag",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("AutoWood", "menu_bag.png"),
            RegionOfInterest = new Rect(0, 0, CaptureRect.Width / 2, CaptureRect.Height),
            DrawOnWindow = false
        }.InitTemplate();

        ConfirmRo = new RecognitionObject
        {
            Name = "AutoWoodConfirm",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("AutoWood", "confirm.png"),
            DrawOnWindow = false
        }.InitTemplate();

        EnterGameRo = new RecognitionObject
        {
            Name = "EnterGame",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("AutoWood", "exit_welcome.png"),
            RegionOfInterest = new Rect(0, CaptureRect.Height / 2, CaptureRect.Width, CaptureRect.Height - CaptureRect.Height / 2),
            DrawOnWindow = false
        }.InitTemplate();
        ExitSwitchRo = new RecognitionObject
        {
            Name = "ExitSwitch",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("AutoWood", "exit_switch.png"),
            RegionOfInterest = new Rect(CaptureRect.Width*9/10, (int)(CaptureRect.Height*0.85), CaptureRect.Width/10,(int)(CaptureRect.Height*0.15)),
            DrawOnWindow = false
        }.InitTemplate();
        ExitPhoneRo = new RecognitionObject
        {
            Name = "ExitPhone",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("AutoWood", "exit_phone.png"),
            RegionOfInterest = new Rect(CaptureRect.Width/3, CaptureRect.Height / 4, CaptureRect.Width/8,CaptureRect.Height/2),
            DrawOnWindow = false
        }.InitTemplate();
    }
}
