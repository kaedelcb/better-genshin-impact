using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.GameTask.Model;
using OpenCvSharp;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Model;


namespace BetterGenshinImpact.GameTask.AutoFight.Assets;

public class AutoFightAssets : BaseAssets<AutoFightAssets>
{
    public Rect TeamRectNoIndex;
    public Rect TeamRect;
    public List<Rect> AvatarSideIconRectList; // 侧边栏角色头像 非联机状态下
    public List<Rect> AvatarIndexRectList; // 侧边栏角色头像对应的白色块 非联机状态下
    public List<Rect> AvatarQRectListMap; // 角色头像对应的Q技能图标 

    public Rect ERect;
    public Rect ECooldownRect;
    public Rect QRect;
    public Rect QRectForClassify;
    public Rect ZCooldownRect;
    public Rect EndTipsUpperRect; // 挑战达成提示
    public Rect EndTipsRect;
    public RecognitionObject WandererIconRa;
    public RecognitionObject WandererIconNoActiveRa;
    public RecognitionObject ConfirmRa;
    public RecognitionObject ConfirmRaZ;
    public RecognitionObject ArtifactAreaRa;
    public RecognitionObject ExitRa;
    public RecognitionObject ClickAnyCloseTipRa;
    public RecognitionObject UseCondensedResinRa;
    public RecognitionObject UseOriginalResinRa;
    public RecognitionObject UseMomentResinRa;
    public RecognitionObject UseFragileResinRa;
    public RecognitionObject SkipanimationRa;
    public RecognitionObject BlackConfirmRa;
    
    // 树脂状态
    public RecognitionObject CondensedResinCountRa;
    public RecognitionObject CondensedResinCountNumRa;
    public RecognitionObject OriginalResinCountRa;
    public RecognitionObject FragileResinCountRa;
    public RecognitionObject MomentResinCountRa;
    
    // public RecognitionObject LockIconRa; // 锁定辅助图标
    public RecognitionObject CondensedResinTopIconRa;
    public RecognitionObject OriginalResinTopIconRa;
    
    public Dictionary<string, string> AvatarCostumeMap;

    // 联机
    public RecognitionObject OnePRa;

    public RecognitionObject PRa;

    /// <summary>
    /// F2 多人游戏页面右侧的红色"踢出玩家"按钮。
    /// 房主自己那行没有，每个已加入世界的成员对应 1 个图标，因此：
    /// 总人数 = 检测到的 KickBtn 数量 + 1（房主）。
    /// </summary>
    public RecognitionObject KickBtnRa;
    public Dictionary<string, List<Rect>> AvatarSideIconRectListMap; // 侧边栏角色头像 联机状态下
    public Dictionary<string, List<Rect>> AvatarIndexRectListMap; // 侧边栏角色头像对应的白色块 联机状态下

    // 小道具位置
    public Rect GadgetRect;
    public RecognitionObject NutritionBagRa;

    public RecognitionObject AbnormalIconRa;
    
    // 原有万叶拾取逻辑使用的单个经验模板
    public RecognitionObject ExperienceRa;

    // 地图追踪奖励结束检测复用的模板对象，避免 200ms 检测循环反复加载图片。
    public List<RecognitionObject> ExperienceRewardRas;
    public List<RecognitionObject> MoraRewardRas;
    
    private  Vanara.PInvoke.RECT _gameScreenSize = SystemControl.GetGameScreenRect(TaskContext.Instance().GameHandle);

#pragma warning disable CS8618 // 在退出构造函数时，不可为 null 的字段必须包含非 null 值。请考虑添加 "required" 修饰符或声明为可为 null。
    private AutoFightAssets() : base()
    {
        Initialization(this.systemInfo);
    }

    protected AutoFightAssets(ISystemInfo systemInfo) : base(systemInfo)
    {
        Initialization(systemInfo);
    }
#pragma warning restore CS8618 // 在退出构造函数时，不可为 null 的字段必须包含非 null 值。请考虑添加 "required" 修饰符或声明为可为 null。

    private void Initialization(ISystemInfo systemInfo)
    {
        TeamRectNoIndex = new Rect(CaptureRect.Width - (int)(355 * AssetScale), (int)(220 * AssetScale),
            (int)((355 - 85) * AssetScale), (int)(465 * AssetScale));
        TeamRect = new Rect(CaptureRect.Width - (int)(355 * AssetScale), (int)(220 * AssetScale),
            (int)(355 * AssetScale), (int)(465 * AssetScale));
        ERect = new Rect(CaptureRect.Width - (int)(267 * AssetScale), CaptureRect.Height - (int)(132 * AssetScale),
            (int)(77 * AssetScale), (int)(77 * AssetScale));
        ECooldownRect = new Rect(CaptureRect.Width - (int)(241 * AssetScale), CaptureRect.Height - (int)(97 * AssetScale),
            (int)(41 * AssetScale), (int)(18 * AssetScale));
        QRect = new Rect(CaptureRect.Width - (int)(157 * AssetScale), CaptureRect.Height - (int)(165 * AssetScale),
            (int)(110 * AssetScale), (int)(110 * AssetScale));
        QRectForClassify = new Rect(CaptureRect.Width - (int)(172 * AssetScale), CaptureRect.Height - (int)(166 * AssetScale),
            (int)(137 * AssetScale), (int)(137 * AssetScale));
        ZCooldownRect = new Rect(CaptureRect.Width - (int)(130 * AssetScale), (int)(814 * AssetScale),
            (int)(60 * AssetScale), (int)(24 * AssetScale));
        // 小道具位置 1920-133,800,60,50
        GadgetRect = new Rect(CaptureRect.Width - (int)(133 * AssetScale), (int)(800 * AssetScale),
            (int)(60 * AssetScale), (int)(50 * AssetScale));
        // 结束提示从中间开始找相对位置
        EndTipsUpperRect = new Rect(CaptureRect.Width / 2 - (int)(100 * AssetScale), (int)(243 * AssetScale),
            (int)(200 * AssetScale), (int)(50 * AssetScale));
        EndTipsRect = new Rect(CaptureRect.Width / 2 - (int)(200 * AssetScale), CaptureRect.Height - (int)(160 * AssetScale),
            (int)(400 * AssetScale), (int)(80 * AssetScale));

        AvatarIndexRectList =
        [
            new Rect(CaptureRect.Width - (int)(61 * AssetScale), (int)(256 * AssetScale), (int)(28 * AssetScale), (int)(24 * AssetScale)),
            new Rect(CaptureRect.Width - (int)(61 * AssetScale), (int)(352 * AssetScale), (int)(28 * AssetScale), (int)(24 * AssetScale)),
            new Rect(CaptureRect.Width - (int)(61 * AssetScale), (int)(448 * AssetScale), (int)(28 * AssetScale), (int)(24 * AssetScale)),
            new Rect(CaptureRect.Width - (int)(61 * AssetScale), (int)(544 * AssetScale), (int)(28 * AssetScale), (int)(24 * AssetScale)),
        ];

        AvatarQRectListMap =
        [
            new Rect(CaptureRect.Width - (int)(336 * AssetScale), (int)(216 * AssetScale), (int)(64 * AssetScale), (int)(84 * AssetScale)),
            new Rect(CaptureRect.Width - (int)(336 * AssetScale), (int)(316 * AssetScale), (int)(64 * AssetScale), (int)(84 * AssetScale)),
            new Rect(CaptureRect.Width - (int)(336 * AssetScale), (int)(416 * AssetScale), (int)(64 * AssetScale), (int)(84 * AssetScale)),
            new Rect(CaptureRect.Width - (int)(336 * AssetScale), (int)(516 * AssetScale), (int)(64 * AssetScale), (int)(84 * AssetScale)),
        ];

        AvatarSideIconRectList =
        [
            new Rect(CaptureRect.Width - (int)(155 * AssetScale), (int)(225 * AssetScale), (int)(76 * AssetScale), (int)(76 * AssetScale)),
            new Rect(CaptureRect.Width - (int)(155 * AssetScale), (int)(315 * AssetScale), (int)(76 * AssetScale), (int)(76 * AssetScale)),
            new Rect(CaptureRect.Width - (int)(155 * AssetScale), (int)(410 * AssetScale), (int)(76 * AssetScale), (int)(76 * AssetScale)),
            new Rect(CaptureRect.Width - (int)(155 * AssetScale), (int)(500 * AssetScale), (int)(76 * AssetScale), (int)(76 * AssetScale)),
        ];

        AvatarCostumeMap = new Dictionary<string, string>
        {
            { "Flamme", "殷红终夜" },
            { "Bamboo", "雨化竹身" },
            { "Dai", "冷花幽露" },
            { "Yu", "玄玉瑶芳" },
            { "Dancer", "帆影游风" },
            { "Witch", "琪花星烛" },
            { "Wic", "和谐" },
            { "Studentin", "叶隐芳名" },
            { "Fruhling", "花时来信" },
            { "Highness", "极夜真梦" },
            { "Feather", "霓裾翩跹" },
            { "Floral", "纱中幽兰" },
            { "Summertime", "闪耀协奏" },
            { "Sea", "海风之梦" },
            { "NightWhisper", "星与烟帷的夜语" },
            { "Holmes", "赫尔洛克变奏曲" },
        };

        // 联机
        // 1p_2 与 p_2 为同一位置
        // 1p_4 与 p_4 为同一位置
        AvatarSideIconRectListMap = new Dictionary<string, List<Rect>>
        {
            {
                "1p_2", [
                    new Rect(CaptureRect.Width - (int)(155 * AssetScale), (int)(375 * AssetScale), (int)(76 * AssetScale), (int)(76 * AssetScale)),
                    new Rect(CaptureRect.Width - (int)(155 * AssetScale), (int)(470 * AssetScale), (int)(76 * AssetScale), (int)(76 * AssetScale)),
                ]
            },
            {
                "1p_3", [
                    new Rect(CaptureRect.Width - (int)(155 * AssetScale), (int)(419 * AssetScale), (int)(76 * AssetScale), (int)(76 * AssetScale)),
                    new Rect(CaptureRect.Width - (int)(155 * AssetScale), (int)(514 * AssetScale), (int)(76 * AssetScale), (int)(76 * AssetScale)),
                ]
            },
            { "1p_4", [new Rect(CaptureRect.Width - (int)(155 * AssetScale), (int)(515 * AssetScale), (int)(76 * AssetScale), (int)(76 * AssetScale))] },
            
            {
                "p_2", [
                    new Rect(CaptureRect.Width - (int)(155 * AssetScale), (int)(375 * AssetScale), (int)(76 * AssetScale), (int)(76 * AssetScale)),
                    new Rect(CaptureRect.Width - (int)(155 * AssetScale), (int)(470 * AssetScale), (int)(76 * AssetScale), (int)(76 * AssetScale)),
                ]
            },
            { "p_3", [new Rect(CaptureRect.Width - (int)(155 * AssetScale), (int)(475 * AssetScale), (int)(76 * AssetScale), (int)(76 * AssetScale))] },
            { "p_4", [new Rect(CaptureRect.Width - (int)(155 * AssetScale), (int)(515 * AssetScale), (int)(76 * AssetScale), (int)(76 * AssetScale))] },
        };

        AvatarIndexRectListMap = new Dictionary<string, List<Rect>>
        {
            {
                "1p_2", [
                    new Rect(CaptureRect.Width - (int)(61 * AssetScale), (int)(412 * AssetScale), (int)(28 * AssetScale), (int)(24 * AssetScale)),
                    new Rect(CaptureRect.Width - (int)(61 * AssetScale), (int)(508 * AssetScale), (int)(28 * AssetScale), (int)(24 * AssetScale)),
                ]
            },
            {
                "1p_3", [
                    new Rect(CaptureRect.Width - (int)(61 * AssetScale), (int)(459 * AssetScale), (int)(28 * AssetScale), (int)(24 * AssetScale)),
                    new Rect(CaptureRect.Width - (int)(61 * AssetScale), (int)(555 * AssetScale), (int)(28 * AssetScale), (int)(24 * AssetScale)),
                ]
            },
            { "1p_4", [new Rect(CaptureRect.Width - (int)(61 * AssetScale), (int)(552 * AssetScale), (int)(28 * AssetScale), (int)(24 * AssetScale))] },
            {
                "p_2", [
                    new Rect(CaptureRect.Width - (int)(61 * AssetScale), (int)(412 * AssetScale), (int)(28 * AssetScale), (int)(24 * AssetScale)),
                    new Rect(CaptureRect.Width - (int)(61 * AssetScale), (int)(508 * AssetScale), (int)(28 * AssetScale), (int)(24 * AssetScale)),
                ]
            },
            { "p_3", [new Rect(CaptureRect.Width - (int)(61 * AssetScale), (int)(412 * AssetScale), (int)(28 * AssetScale), (int)(24 * AssetScale))] },
            { "p_4", [new Rect(CaptureRect.Width - (int)(61 * AssetScale), (int)(507 * AssetScale), (int)(28 * AssetScale), (int)(24 * AssetScale))] },
        };

        // 左上角的 1P 图标
        OnePRa = new RecognitionObject
        {
            Name = "1P",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("AutoFight", "1p.png", this.systemInfo),
            RegionOfInterest = new Rect(0, 0, CaptureRect.Width / 4, CaptureRect.Height / 7),
            DrawOnWindow = false
        }.InitTemplate();
        // 右侧联机的 P 图标
        PRa = new RecognitionObject
        {
            Name = "P",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("AutoFight", "p.png", this.systemInfo),
            RegionOfInterest = new Rect(CaptureRect.Width - (int)(CaptureRect.Width / 12.5), CaptureRect.Height / 5, (int)(CaptureRect.Width / 12.5), CaptureRect.Height / 2 - CaptureRect.Width / 7),
            DrawOnWindow = false
        }.InitTemplate();

        // F2 多人游戏页面右侧的红色"踢出玩家"按钮
        // ROI 限定在右半屏的中上部，覆盖 1P~4P 行的踢出按钮区域，避开右下角"回到单人模式"按钮干扰 
        KickBtnRa = new RecognitionObject
        {
            Name = "KickBtn",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("AutoFight", "kick_btn.png", this.systemInfo),
            RegionOfInterest = new Rect(CaptureRect.Width/2, 0, CaptureRect.Width/2, CaptureRect.Height),
            // 三通道（BGR）匹配：踢出按钮为红色、旁边"加为好友"为绿色。默认灰度匹配会把红/绿抹成
            // 相近亮度导致误匹配到绿色按钮（此时提高 Threshold 也无效，因两者灰度分数都很高）。
            // 开启三通道让颜色重新参与判定，区分红/绿。
            Use3Channels = true,
            Threshold = 0.85,
            DrawOnWindow = true
        }.InitTemplate();

        WandererIconRa = new RecognitionObject
        {
            Name = "WandererIcon",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("AutoFight", "wanderer_icon.png", this.systemInfo),
            DrawOnWindow = false
        }.InitTemplate();
        WandererIconNoActiveRa = new RecognitionObject
        {
            Name = "WandererIconNoActive",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("AutoFight", "wanderer_icon_no_active.png", this.systemInfo),
            DrawOnWindow = false
        }.InitTemplate();

        // 右下角的按钮
        ConfirmRa = new RecognitionObject
        {
            Name = "Confirm",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("AutoFight", "confirm.png", this.systemInfo),
            RegionOfInterest = new Rect(CaptureRect.Width / 2, CaptureRect.Height / 2, CaptureRect.Width / 2, CaptureRect.Height / 2),
            DrawOnWindow = false
        }.InitTemplate();
        
        // 右下角的按钮
        ConfirmRaZ = new RecognitionObject
        {
            Name = "Confirm",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("AutoFight", "confirm.png", this.systemInfo),
            RegionOfInterest = new Rect(0, CaptureRect.Height / 2, CaptureRect.Width / 2, CaptureRect.Height / 3),
            DrawOnWindow = false
        }.InitTemplate();
        ArtifactAreaRa = new RecognitionObject
        {
            Name = "ArtifactArea",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("AutoFight", "artifact_flower_logo.png", this.systemInfo),
            RegionOfInterest = new Rect(CaptureRect.Width / 2, 0, CaptureRect.Width / 2, CaptureRect.Height),
            DrawOnWindow = false
        }.InitTemplate();

        // 点击任意处关闭提示
        ClickAnyCloseTipRa = new RecognitionObject
        {
            Name = "ClickAnyCloseTip",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("AutoFight", "click_any_close_tip.png", this.systemInfo),
            RegionOfInterest = new Rect(0, CaptureRect.Height / 2, CaptureRect.Width, CaptureRect.Height / 2),
            DrawOnWindow = false
        }.InitTemplate();

        //识别使用树脂的按钮，改识别位置和图片
        UseCondensedResinRa = new RecognitionObject
        {
            Name = "UseCondensedResin",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("AutoFight", "use_condensed_resin.png"),
            RegionOfInterest = new Rect(CaptureRect.Width / 4, CaptureRect.Height / 4, CaptureRect.Width / 4, CaptureRect.Height / 2),
            DrawOnWindow = false
        }.InitTemplate();
        
        UseOriginalResinRa = new RecognitionObject
        {
            Name = "UseOriginalResin",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("AutoFight", "use_original_resin.png"),
            RegionOfInterest = new Rect(CaptureRect.Width / 4, CaptureRect.Height / 4, CaptureRect.Width / 4, CaptureRect.Height / 2),
            DrawOnWindow = false
        }.InitTemplate();
        
        UseFragileResinRa = new RecognitionObject
        {
            Name = "UseFragileResin",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("AutoFight", "use_fragile_resin.png"),
            RegionOfInterest = new Rect(CaptureRect.Width / 4, CaptureRect.Height / 4, CaptureRect.Width / 4, CaptureRect.Height / 2),
            DrawOnWindow = false
        }.InitTemplate();

        UseMomentResinRa = new RecognitionObject
        {
            Name = "UseMomentResin",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("AutoFight", "use_moment_resin.png"),
            RegionOfInterest = new Rect(CaptureRect.Width / 4, CaptureRect.Height / 4, CaptureRect.Width / 4, CaptureRect.Height / 2),
            DrawOnWindow = false
        }.InitTemplate();

        ExitRa = new RecognitionObject
        {
            Name = "Exit",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("AutoFight", "exit.png", this.systemInfo),
            RegionOfInterest = new Rect(0, CaptureRect.Height / 2, CaptureRect.Width / 2, CaptureRect.Height / 2),
            DrawOnWindow = false
        }.InitTemplate();

        //识别树脂数量，改识别位置和图片
        CondensedResinCountRa = new RecognitionObject //可以识别 √
        {
            Name = "CondensedResinCount",
            UseMask = true,
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("AutoFight", "condensed_resin_count.png"),
            RegionOfInterest = new Rect(CaptureRect.Width / 2, 0, CaptureRect.Width / 2, CaptureRect.Height / 10),
            DrawOnWindow = true
        }.InitTemplate();
        OriginalResinCountRa = new RecognitionObject //可以识别 √
        {
            Name = "OriginalResinCount",
            UseMask = true,
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("AutoFight", "original_resin_count.png"),            
            RegionOfInterest = new Rect(CaptureRect.Width *3/ 5, 0, CaptureRect.Width *2/ 5, CaptureRect.Height / 10),
            DrawOnWindow = true
        }.InitTemplate();
        FragileResinCountRa = new RecognitionObject //可以识别 √
        {
            Name = "FragileResinCount",
            UseMask = true,
            MaskColor = System.Drawing.Color.FromArgb(0, 255, 0), // 绿色
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("AutoFight", "fragile_resin_count.png"),
            RegionOfInterest = new Rect(CaptureRect.Width /2, 0, CaptureRect.Width /2, CaptureRect.Height / 10),
            DrawOnWindow = true
        }.InitTemplate();
        MomentResinCountRa = new RecognitionObject //可以识别 √
        {
            Name = "MomentResinCount",
            UseMask = true,
            MaskColor = System.Drawing.Color.FromArgb(0, 255, 0), // 绿色
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("AutoFight", "moment_resin_count.png"),
            RegionOfInterest = new Rect(CaptureRect.Width /2, 0, CaptureRect.Width / 2, CaptureRect.Height / 10),
            DrawOnWindow = true
        }.InitTemplate();
        SkipanimationRa = new RecognitionObject //可以识别 √
        {
            Name = "Skipanimation",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("AutoFight", "skip_animation.png"),
            RegionOfInterest = new Rect(0, 0, CaptureRect.Width / 10, CaptureRect.Height / 10),
            DrawOnWindow = false
        }.InitTemplate();
        BlackConfirmRa = new RecognitionObject //可以识别 √
        {
            Name = "BlackConfirm",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("AutoFight", "blackconfirm.png"),
            RegionOfInterest = new Rect(CaptureRect.Width / 2, CaptureRect.Height / 2, CaptureRect.Width / 2, CaptureRect.Height / 2),
            DrawOnWindow = false
        }.InitTemplate();
        
        
        CondensedResinTopIconRa = new RecognitionObject
        {
            Name = "CondensedResinTopIcon",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("AutoFight", "condensed_resin_top_icon.png"),
            RegionOfInterest = new Rect((int)(1270 * AssetScale), (int)(25 * AssetScale), (int)(520 * AssetScale), (int)(45 * AssetScale)),
            DrawOnWindow = false
        }.InitTemplate();
        OriginalResinTopIconRa = new RecognitionObject
        {
            Name = "OriginalResinTopIcon",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("AutoFight", "original_resin_top_icon.png"),
            RegionOfInterest = new Rect(CaptureRect.Width - (int)(450 * AssetScale), (int)(25 * AssetScale), (int)(265 * AssetScale), (int)(45 * AssetScale)),
            DrawOnWindow = false
        }.InitTemplate();
        AbnormalIconRa = new RecognitionObject
        {
            Name = "AbnormalIcon",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("AutoFight", "abnormal_icon.png", this.systemInfo),
            RegionOfInterest = new Rect(0, (int)(CaptureRect.Height * 0.08), (int)(CaptureRect.Width * 0.04), (int)(CaptureRect.Height * 0.07)),
            DrawOnWindow = false
        }.InitTemplate();
        NutritionBagRa = new RecognitionObject
        {
            Name = "NutritionBag",
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("AutoFight", "nutrition_bag.png"),
            RegionOfInterest = new Rect((int)(570 * AssetScale), (int)(490 * AssetScale), (int)(780 * AssetScale), (int)(110 * AssetScale)),
            DrawOnWindow = false
        }.InitTemplate();
        ExperienceRewardRas = InitializeExperienceRewardRecognitionObjects();
        MoraRewardRas = InitializeMoraRewardRecognitionObjects();
    }
    
    public RecognitionObject InitializeRecognitionObject(int experience)
    {
        // 保留原万叶拾取检测范围；奖励结束检测使用独立的较小 ROI，不能互相覆盖。
        ExperienceRa = CreateExperienceRecognitionObject(
            experience,
            new Rect((int)(CaptureRect.Width * 0.145), (int)(CaptureRect.Height * 0.5),
                (int)(CaptureRect.Width * 0.02), (int)(CaptureRect.Height * 0.22)));
        return ExperienceRa;
    }

    private RecognitionObject CreateExperienceRecognitionObject(int experience, Rect regionOfInterest)
    {
        var threshold = 0.9;
        
        if (_gameScreenSize.Width > 2560)
        {
            threshold =0.6;
        }
        else if (_gameScreenSize.Width > 1920)
        {
            threshold =0.6;
        }
        
        return new RecognitionObject
        {
            Name = experience.ToString(),
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("AutoFight", "experience_" + experience + ".png"),
            RegionOfInterest = regionOfInterest,
            UseMask = true,
            Threshold = threshold,
            DrawOnWindow = true,
        }.InitTemplate();
    }

    private List<RecognitionObject> InitializeExperienceRewardRecognitionObjects()
    {
        // 奖励结束只扫描左侧经验数值出现区域，避免沿用万叶拾取所需的纵向大范围。
        var region = new Rect(
            (int)(CaptureRect.Width * 0.145),
            (int)(CaptureRect.Height * 0.5),
            (int)(CaptureRect.Width * 0.03),
            (int)(CaptureRect.Height * 0.08));

        return new[] { 180, 120, 60, 58, 57 }
            .Select(experience => CreateExperienceRecognitionObject(experience, region))
            .ToList();
    }

    private List<RecognitionObject> InitializeMoraRewardRecognitionObjects()
    {
        var threshold = 0.86;

        if (_gameScreenSize.Width > 1920)
        {
            threshold = 0.65;
        }

        // 摩拉提示可能纵向堆叠，ROI 覆盖左侧多行奖励数值区域。
        var region = new Rect(
            (int)(CaptureRect.Width * 0.1),
            (int)(CaptureRect.Height * 0.48),
            (int)(CaptureRect.Width * 0.12),
            (int)(CaptureRect.Height * 0.18));

        return new[] { 200, 400, 600, 1200, 3000 }
            .Select(mora => new RecognitionObject
            {
                Name = "mora_" + mora,
                RecognitionType = RecognitionTypes.TemplateMatch,
                TemplateImageMat = GameTaskManager.LoadAssetImage("AutoFight", "mora_" + mora + ".png"),
                RegionOfInterest = region,
                UseMask = true,
                Threshold = threshold,
                DrawOnWindow = true,
                DrawOnWindowPen = new Pen(Color.Lime, 2)
            }.InitTemplate())
            .ToList();
    }
    
    public RecognitionObject InitializeCondensedResin(int condensedResinCount)
    {
        return new RecognitionObject
        {
            Name = condensedResinCount.ToString(),
            RecognitionType = RecognitionTypes.TemplateMatch,
            TemplateImageMat = GameTaskManager.LoadAssetImage("AutoFight", "condensed_resin_count_" + condensedResinCount + ".png"),
            Threshold = 0.9,
        }.InitTemplate();
    }
    
}
