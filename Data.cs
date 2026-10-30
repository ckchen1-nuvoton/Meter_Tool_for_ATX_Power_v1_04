using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Ivi.Visa;
using NationalInstruments.Visa;

namespace Meter_Test_Tool_V1_00
{
    /// <summary>
    /// 功率計 / 電子負載遠端控制工具。
    ///
    /// 透過 VISA 以標準 SCPI 命令擷取 V / I / W / PF / VTHD / ITHD / WH，
    /// 並提供積分（Wh）量測與 DC Load 百分比掃描。
    ///
    /// 設計重點：
    ///  1. 不硬編碼機種能力。連線時「探測」機器實際支援哪些參數，
    ///     不支援的欄位一律顯示 N/A，避免一個不支援的關鍵字讓整條複合查詢失敗。
    ///  2. Chroma 與 Yokogawa 的積分指令集完全不同，各自獨立實作。
    ///  3. 所有 VISA I/O 都在背景執行緒並以 SemaphoreSlim 序列化，UI 不會凍結。
    ///  4. 積分輪詢有逾時上限；DC Load 掃描不論如何結束都會 LOAD OFF。
    /// </summary>
    public partial class Form1 : Form
    {
        #region 常數與型別定義

        /// <summary>探測與一般查詢用的 VISA 逾時（毫秒）。探測用較短，避免一次不支援就等太久。</summary>
        private const int ProbeTimeoutMs = 2000;
        private const int SessionTimeoutMs = 5000;

        /// <summary>積分狀態輪詢週期，以及「設定秒數之外」額外容許的等待時間。</summary>
        private const int PollIntervalMs = 500;
        private const int PollGraceSeconds = 15;

        /// <summary>連續讀取失敗達此次數就自動停止監看，避免在斷線後不斷噴錯。</summary>
        private const int MaxConsecutiveReadFailures = 3;

        /// <summary>Instruments 群組展開／收合時的高度，以及狀態列保留的訊息筆數。</summary>
        private const int VisaExpandedHeight = 82;
        private const int VisaCollapsedHeight = 24;
        private const int LogHistoryLines = 30;

        /// <summary>
        /// 積分掃描時，切到下一個負載點後仍保留上一點讀值的秒數，讓數值停在畫面上可以核對。
        /// 這段與下一點的穩定時間（Delay）重疊 —— 只是那幾秒不刷新畫面，
        /// 不會額外拉長每點的總時間，也不會延後積分開始的時刻。
        /// </summary>
        private const int SweepPointBufferSeconds = 5;

        /// <summary>Yokogawa 對「無資料 / 不可用」回傳 9.91E+37，需視為 N/A。</summary>
        private const double YokogawaNoData = 9.9e37;

        /// <summary>
        /// 計算效率時輸入功率（功率計 W）至少要大於這個值才有意義。
        /// 未上電或負載 0% 時輸入功率可能接近 0，直接相除會得出無意義的巨大數字。
        /// </summary>
        private const double MinEfficiencyInputWatt = 0.001;

        /// <summary>效率超過這個值一定是接線或量測端搞錯（輸出不可能大於輸入），顯示為紅字提醒。</summary>
        private const double SuspiciousEfficiencyPct = 100.0;

        private enum MeterVendor { Unknown, Chroma, Yokogawa }

        /// <summary>本工具支援擷取的量測項目。順序即為 UI 與複合查詢的欄位順序。</summary>
        private enum Quantity { Voltage, Current, Watt, PowerFactor, Vthd, Ithd, WattHour }

        private static readonly Quantity[] AllQuantities =
        {
            Quantity.Voltage, Quantity.Current, Quantity.Watt,
            Quantity.PowerFactor, Quantity.Vthd, Quantity.Ithd, Quantity.WattHour
        };

        /// <summary>
        /// 瞬時量：不像 WH 會隨積分累積，積分結束那一瞬間的單筆讀值沒有代表性。
        /// 因此積分期間持續取樣，最後取平均值寫進 Result。
        /// </summary>
        private static readonly Quantity[] ThdQuantities = { Quantity.Vthd, Quantity.Ithd };

        /// <summary>
        /// Chroma 積分的觸發模式候選。TRIGger 是共用的觸發入口，啟動積分前必須先指定
        /// 它要觸發哪個功能；參數名稱各機種／韌體不一致，因此逐一試出可用的那個。
        /// </summary>
        private static readonly string[] ChromaTriggerModes =
        {
            "TRIGger:MODE INTEGRATION",
            "TRIGger:MODE ENERGY",
        };

        /// <summary>Chroma 662xx 的 FETCh?/MEASure? 參數關鍵字。</summary>
        private static readonly Dictionary<Quantity, string> ChromaKeyword = new Dictionary<Quantity, string>
        {
            { Quantity.Voltage,     "V"    },
            { Quantity.Current,     "I"    },
            { Quantity.Watt,        "W"    },
            { Quantity.PowerFactor, "PF"   },
            { Quantity.Vthd,        "THDV" },
            { Quantity.Ithd,        "THDI" },
            { Quantity.WattHour,    "WH"   },
        };

        /// <summary>
        /// Yokogawa :NUMeric:NORMal:ITEM 的功能名稱候選清單。
        /// WT200/WT210 與 WT310/WT330 對同一個物理量的命名不完全一致（例如 U vs URMS），
        /// 且 THD 需要諧波量測選配才存在，因此逐一嘗試、取第一個機器真的接受的名稱。
        /// </summary>
        private static readonly Dictionary<Quantity, string[]> YokogawaItems = new Dictionary<Quantity, string[]>
        {
            { Quantity.Voltage,     new[] { "URMS", "U" }            },
            { Quantity.Current,     new[] { "IRMS", "I" }            },
            { Quantity.Watt,        new[] { "P" }                    },
            { Quantity.PowerFactor, new[] { "LAMB", "PF" }           },
            { Quantity.Vthd,        new[] { "UTHD", "UHDF", "THDU" } },
            { Quantity.Ithd,        new[] { "ITHD", "IHDF", "THDI" } },
            { Quantity.WattHour,    new[] { "WH" }                   },
        };

        // ===== 儀器判別規則（沿用原專案；新增機種只要在這裡各加一行）=====
        // 62 = Chroma 62000D 雙向電源（Source / Load 兩用）；63 = Chroma 63xxx 電子負載。
        private static readonly string[] DcLoadModelPrefixes     = { "62", "63" };    // Chroma 電子負載家族
        private static readonly string[] PowerMeterModelPrefixes = { "66", "WT" };    // Chroma 功率計 / Yokogawa WT
        private static readonly string[] DcLoadUsbPids           = { "0X0879" };      // Chroma 632xx
        private static readonly string[] PowerMeterUsbPids       = { "0X0835" };      // Chroma 662xx

        /// <summary>下拉選單項目：顯示友善名稱，實際使用 VISA 位址。</summary>
        private class VisaDevice
        {
            public string Address { get; }
            public string DisplayName { get; }
            public VisaDevice(string address, string displayName = null)
            {
                Address = address;
                DisplayName = displayName ?? address;
            }
            public override string ToString() => DisplayName;
        }

        /// <summary>某一台功率計的探測結果：廠商、型號，以及實際可用的參數與其關鍵字。</summary>
        private class MeterProfile
        {
            public string Address;
            public string Idn = "";
            public string Model = "";
            public MeterVendor Vendor = MeterVendor.Unknown;

            /// <summary>探測後確認可用的量測項目，維持 AllQuantities 的順序。</summary>
            public readonly List<Quantity> Supported = new List<Quantity>();

            /// <summary>Yokogawa 專用：該量測項目最終採用的 ITEM 名稱。</summary>
            public readonly Dictionary<Quantity, string> ItemName = new Dictionary<Quantity, string>();

            /// <summary>是否可用「一次複合查詢取回全部欄位」（最快）；否則逐項查詢。</summary>
            public bool CompositeOk;

            /// <summary>
            /// 這台機器可用的「積分歸零」命令；null 代表沒有獨立的歸零命令
            /// （Chroma 是靠 TRIGger OFF 停止並重置）。
            /// </summary>
            public string EnergyResetCommand;

            /// <summary>Chroma 專用：第一次啟動積分時試出來的 TRIGger:MODE 命令，之後沿用。</summary>
            public string TriggerModeCommand;

            public bool SupportsEnergy => Supported.Contains(Quantity.WattHour);
        }

        /// <summary>
        /// 一組 DC Load 命令。Chroma 兩個負載家族的命令完全不同，不能共用：
        ///
        ///   63xxx  電子負載        CURR:STAT:L1 &lt;A&gt;   /  LOAD ON|OFF
        ///   62000D 雙向電源        LOAD:CURRent &lt;A&gt;   /  CONFigure:OUTPut ON|OFF，
        ///                          且拉載前必須先 SYSTem:MODE LOAD 把機器切到負載模式。
        ///
        /// 送出機器不認識的命令時，Chroma 只會把錯誤丟進佇列、不做任何動作，
        /// 外觀上就是「按了 Start Sweep 卻完全沒反應」。因此命令族必須在連線時就確定。
        /// </summary>
        private class LoadCommandSet
        {
            public string Name;
            /// <summary>電流設定命令的前綴，後面接空白與安培值。</summary>
            public string SetCurrent;
            /// <summary>探測用：能正常回應此查詢，代表機器屬於這一族。</summary>
            public string QueryCurrent;
            public string OutputOn;
            public string OutputOff;
            /// <summary>拉載前要先送的模式切換命令；null 代表這一族不需要切模式。</summary>
            public string EnterLoadMode;
            /// <summary>型號前綴符合時優先試這一族，避免對不支援的機器送查詢白等一次逾時。</summary>
            public string[] ModelPrefixes;

            // ===== 拉載中的即時讀值（負載端 = 待測物的輸出側）=====
            // FETCh 直接從量測緩衝區取最近一次的結果，最快；
            // MEASure 會先觸發一次新的擷取再回傳，較慢但一定是當下重新量到的值。
            // 兩者的參數名稱在 Chroma 63xxx 與 62000D 上一致，因此給同一組預設值；
            // 日後若有機種寫法不同，在該命令族裡覆寫這幾個欄位即可。
            public string FetchVoltage = "FETCh:VOLTage?";
            public string FetchCurrent = "FETCh:CURRent?";
            public string FetchPower   = "FETCh:POWer?";
            public string MeasVoltage  = "MEASure:VOLTage?";
            public string MeasCurrent  = "MEASure:CURRent?";
            public string MeasPower    = "MEASure:POWer?";
        }

        /// <summary>
        /// 支援的 DC Load 命令族。新增機種就在這裡加一組，其餘流程不必改。
        /// 順序只影響「型號認不出來時的嘗試順序」。
        /// </summary>
        private static readonly LoadCommandSet[] LoadCommandSets =
        {
            new LoadCommandSet
            {
                Name          = "Chroma 63xxx 電子負載",
                SetCurrent    = "CURR:STAT:L1",
                QueryCurrent  = "CURR:STAT:L1?",
                OutputOn      = "LOAD ON",
                OutputOff     = "LOAD OFF",
                EnterLoadMode = null,
                ModelPrefixes = new[] { "63" },
            },
            new LoadCommandSet
            {
                Name          = "Chroma 62000D 雙向電源（Load 模式）",
                SetCurrent    = "LOAD:CURRent",
                QueryCurrent  = "LOAD:CURRent?",
                OutputOn      = "CONFigure:OUTPut ON",
                OutputOff     = "CONFigure:OUTPut OFF",
                EnterLoadMode = "SYSTem:MODE LOAD",
                ModelPrefixes = new[] { "62" },
            },
        };

        /// <summary>某一台 DC Load 的探測結果：型號，以及確認可用的命令族。</summary>
        private class LoadProfile
        {
            public string Idn = "";
            public string Model = "";
            public LoadCommandSet Commands;
            /// <summary>命令族是靠查詢確認的，還是查詢失敗後只能依型號推定。</summary>
            public bool Verified;

            /// <summary>連線時探測出來的即時讀值能力，不支援的項目一律顯示 N/A。</summary>
            public bool CanReadVoltage;
            public bool CanReadCurrent;
            public bool CanReadPower;

            /// <summary>機器不回傳功率、但電壓電流都讀得到時，功率改由 V × I 推算。</summary>
            public bool CanComputePower => !CanReadPower && CanReadVoltage && CanReadCurrent;

            public bool ReadbackOk => CanReadVoltage || CanReadCurrent || CanReadPower;
        }

        /// <summary>
        /// DC Load 端的一組即時讀值（待測物的輸出側）。
        ///
        /// 拉載時機器回傳的電流與功率帶負號（表示電流是逆灌 / 汲取），
        /// 但這裡要的是「負載實際吃掉多少功率」，因此一律取絕對值後再存進來，
        /// 讓畫面、Result 表與效率計算三者的數值定義一致。
        /// </summary>
        private class LoadReading
        {
            public double? Voltage;
            public double? Current;
            public double? Watt;
            /// <summary>Watt 是機器回傳的，還是由 V × I 推算出來的。</summary>
            public bool WattComputed;

            public bool Any => Voltage.HasValue || Current.HasValue || Watt.HasValue;
        }

        /// <summary>積分期間對瞬時量做累加求平均的小工具。</summary>
        private class Averager
        {
            private readonly Dictionary<Quantity, double> _sum = new Dictionary<Quantity, double>();
            private readonly Dictionary<Quantity, int> _count = new Dictionary<Quantity, int>();

            public void Add(Quantity q, double? v)
            {
                if (!v.HasValue || double.IsNaN(v.Value) || double.IsInfinity(v.Value)) return;
                double s; _sum.TryGetValue(q, out s); _sum[q] = s + v.Value;
                int c; _count.TryGetValue(q, out c); _count[q] = c + 1;
            }

            public int Samples(Quantity q) { int c; return _count.TryGetValue(q, out c) ? c : 0; }

            public double? Mean(Quantity q)
            {
                int c = Samples(q);
                return c > 0 ? (double?)(_sum[q] / c) : null;
            }
        }

        /// <summary>一輪積分的結果：最終讀值（VTHD/ITHD 已換成期間平均）與實際取樣筆數。</summary>
        private class IntegrationResult
        {
            public Dictionary<Quantity, double?> Values;
            public int ThdSamples;
        }

        #endregion

        #region 欄位

        private NationalInstruments.Visa.ResourceManager _rm;

        private MessageBasedSession _meter;
        private MessageBasedSession _load;
        private MeterProfile _profile;
        private LoadProfile _loadProfile;
        private string _loadModel = "";

        // Instruments 群組收合狀態，以及狀態列的訊息歷程（滑鼠移到狀態列可看前幾筆）。
        private bool _visaExpanded = true;
        private readonly Queue<string> _logHistory = new Queue<string>();

        // 上次掃描中「確定認出型別」的裝置位址，一鍵連線據此決定要連幾台。
        private List<string> _identifiedMeters = new List<string>();
        private List<string> _identifiedLoads = new List<string>();

        // MessageBasedSession 非執行緒安全，所有存取都要先取得對應的號誌。
        private readonly SemaphoreSlim _meterIo = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _loadIo = new SemaphoreSlim(1, 1);

        private CancellationTokenSource _monitorCts;
        private CancellationTokenSource _integCts;
        private CancellationTokenSource _sweepCts;

        private readonly Dictionary<Quantity, TextBox> _readouts = new Dictionary<Quantity, TextBox>();

        /// <summary>
        /// DC Load 讀值要用 MEASure（觸發新量測）還是 FETCh（取緩衝區現值）。
        /// 由 UI 執行緒的 CheckedChanged 寫入、背景 I/O 讀取，
        /// 不直接在背景執行緒碰 CheckBox.Checked（跨執行緒存取控制項）。
        /// </summary>
        private volatile bool _loadUseMeasure;

        /// <summary>負載讀值失敗只提示一次，避免掃描期間每秒洗版。</summary>
        private bool _loadReadErrorLogged;

        /// <summary>
        /// 手動拉載（Load ON / OFF 按鈕）目前的狀態。掃描流程自己會開關負載，
        /// 結束時一併把這個旗標歸位，畫面上的按鈕才不會停在與機器相反的狀態。
        /// </summary>
        private bool _loadOutputOn;

        /// <summary>手動電流輸入正在寫入機器；連續轉動上下鈕時用來合併中間的過渡值。</summary>
        private bool _loadSetBusy;

        #endregion

        public Form1()
        {
            InitializeComponent();
            this.FormClosing += Form1_FormClosing;
        }

        #region 表單生命週期

        private void Form1_Load(object sender, EventArgs e)
        {
            try { this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            _readouts[Quantity.Voltage]     = txtV;
            _readouts[Quantity.Current]     = txtI;
            _readouts[Quantity.Watt]        = txtW;
            _readouts[Quantity.PowerFactor] = txtPF;
            _readouts[Quantity.Vthd]        = txtVthd;
            _readouts[Quantity.Ithd]        = txtIthd;
            _readouts[Quantity.WattHour]    = txtWh;

            InitResultGrid();
            ClearReadouts();

            try
            {
                _rm = new NationalInstruments.Visa.ResourceManager();
            }
            catch (Exception ex)
            {
                _rm = null;
                Log("無法初始化 NI-VISA：" + ex.Message);
                SetStatus("NI-VISA driver 未安裝或無法載入，儀器功能停用。");
            }

            SetVisaExpanded(true);
            UpdateUiState();
            ScanVisaDevices();
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            // 最後保險：關閉前務必把負載歸零關掉、儀器切回本機，不要讓機器繼續拉載或鎖在遠端模式。
            try { ShutdownInstruments(); } catch { }
            try { _rm?.Dispose(); } catch { }
        }

        private void CancelAll()
        {
            try { _monitorCts?.Cancel(); } catch { }
            try { _integCts?.Cancel(); } catch { }
            try { _sweepCts?.Cancel(); } catch { }
        }

        #endregion

        #region VISA 基礎工具

        private static void ConfigureSession(MessageBasedSession s, int timeoutMs)
        {
            s.TimeoutMilliseconds = timeoutMs;
            // 以 LF 為結束字元，讓 ReadString() 讀到一行就返回，不必等到逾時。
            try
            {
                s.TerminationCharacter = 0x0A;
                s.TerminationCharacterEnabled = true;
            }
            catch { /* 少數介面不支援設定終止字元，忽略即可 */ }
        }

        private MessageBasedSession OpenSession(string address, int timeoutMs)
        {
            if (_rm == null) throw new InvalidOperationException("NI-VISA 尚未初始化。");
            var s = (MessageBasedSession)_rm.Open(address);
            ConfigureSession(s, timeoutMs);
            return s;
        }

        /// <summary>
        /// 探測過程中若查詢逾時，儀器的輸出緩衝可能殘留不完整資料，
        /// 後續讀取會整批位移。重新開 session 是最乾淨的復原方式。
        /// </summary>
        private void ReopenSession(ref MessageBasedSession s, string address, int timeoutMs)
        {
            try { s?.Dispose(); } catch { }
            s = OpenSession(address, timeoutMs);
        }

        private static void DisposeSession(ref MessageBasedSession s)
        {
            try { s?.Dispose(); } catch { }
            s = null;
        }

        /// <summary>
        /// 把儀器切回本機（Local）模式，解除前面板鎖定。**務必在 Dispose session 之前呼叫。**
        ///
        /// USB488 / USBTMC 的機器只要收到任何一次寫入就會自動進入 REMOTE 並把前面板鎖住，
        /// 而關閉 VISA session 不會解除這個狀態 —— 少了這一步，程式斷線（甚至只是掃描一次）
        /// 之後機器就一直卡在遠端控制，實務上只能拔 USB 才能恢復操作。
        ///
        /// 兩層都要做：
        ///   1. SCPI 命令：廠商各自的「回本機」命令，讓機器自己切換狀態。
        ///   2. VISA GOTO_LOCAL：USBTMC 協定層的控制傳輸，真正解除 REN 造成的面板鎖定。
        /// 順序不能顛倒 —— GOTO_LOCAL 之後再送任何命令，機器會立刻又回到 REMOTE。
        /// </summary>
        /// <returns>VISA 層的 GOTO_LOCAL 是否成功送出。</returns>
        private static bool ReturnToLocal(MessageBasedSession s, MeterVendor vendor)
        {
            if (s == null) return false;

            // 廠商不明時兩條都送：機器不認識的那一條只會留在錯誤佇列，不影響另一條生效。
            if (vendor != MeterVendor.Yokogawa) TryWrite(s, "SYSTem:LOCal");            // Chroma 等多數 SCPI 儀器
            if (vendor != MeterVendor.Chroma)   TryWrite(s, ":COMMunicate:REMote OFF"); // Yokogawa WT 系列

            try
            {
                var usb = s as IUsbSession;
                if (usb != null) { usb.SendRemoteLocalCommand(RemoteLocalMode.Local); return true; }

                var gpib = s as IGpibSession;
                if (gpib != null)
                {
                    gpib.SendRemoteLocalCommand(GpibInstrumentRemoteLocalMode.GoToLocal);
                    return true;
                }
            }
            catch { /* 少數介面 / 驅動不支援 GOTO_LOCAL，此時只能靠上面的 SCPI 命令 */ }
            return false;
        }

        private static void Write(MessageBasedSession s, string cmd)
        {
            s.RawIO.Write(cmd.EndsWith("\n") ? cmd : cmd + "\n");
        }

        /// <summary>送出命令但忽略失敗，用於「可有可無」的設定與收尾動作。</summary>
        private static void TryWrite(MessageBasedSession s, string cmd)
        {
            try { Write(s, cmd); } catch { }
        }

        private static string Query(MessageBasedSession s, string cmd)
        {
            Write(s, cmd);
            return s.RawIO.ReadString().Trim();
        }

        private static double? ParseNumber(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string t = text.Trim().Trim('"');
            double v;
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return null;
        }

        /// <summary>把逗號分隔的回應拆成數值清單；無法解析的欄位保留為 null 以維持位置對應。</summary>
        private static List<double?> ParseList(string text)
        {
            var list = new List<double?>();
            if (string.IsNullOrWhiteSpace(text)) return list;
            var tokens = text.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var tok in tokens) list.Add(ParseNumber(tok));
            return list;
        }

        private static double? Sanitize(double? v)
        {
            if (!v.HasValue) return null;
            if (double.IsNaN(v.Value) || double.IsInfinity(v.Value)) return null;
            if (Math.Abs(v.Value) >= YokogawaNoData) return null;   // Yokogawa 的「無資料」代碼
            return v;
        }

        /// <summary>查詢 SCPI 錯誤佇列；非 0 代表上一個命令機器不認識或執行失敗。</summary>
        private static bool HasScpiError(MessageBasedSession s)
        {
            string e = Query(s, "SYSTem:ERRor?");
            if (string.IsNullOrEmpty(e)) return false;
            double code;
            string first = e.Split(',')[0].Trim();
            if (double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out code))
                return Math.Abs(code) > 0.5;
            return false;   // 回應格式看不懂時不判定為錯誤，交由數值本身是否可解析決定
        }

        /// <summary>
        /// 讀一次錯誤佇列並記錄內容。Chroma 收到不認識的命令不會有任何外顯反應，
        /// 只會在佇列留下記錄 —— 少了這一步，命令送錯時看起來就只是「按了完全沒反應」。
        /// 必須在已取得對應號誌的情況下呼叫。
        /// </summary>
        private void CheckScpiError(MessageBasedSession s, string device, string context)
        {
            try
            {
                string e = Query(s, "SYSTem:ERRor?");
                if (string.IsNullOrEmpty(e)) return;

                double code;
                string first = e.Split(',')[0].Trim();
                if (double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out code)
                    && Math.Abs(code) > 0.5)
                    Log($"{device}回報錯誤 → {e}    （命令：{context}）");
            }
            catch { /* 讀不到錯誤佇列不影響測試進行 */ }
        }

        private void CheckLoadError(string context)
        {
            CheckScpiError(_load, "DC Load ", context);
        }

        /// <summary>送出命令並以錯誤佇列確認機器接受；用於「有多個候選寫法，試出可用的那個」。</summary>
        private static bool TryCommand(MessageBasedSession s, string cmd)
        {
            try
            {
                TryWrite(s, "*CLS");
                Write(s, cmd);
                return !HasScpiError(s);
            }
            catch { return false; }
        }

        #endregion

        #region 儀器掃描

        private static string InstrModel(string idn)
        {
            if (string.IsNullOrEmpty(idn)) return "";
            var parts = idn.Split(',');
            return parts.Length >= 2 ? parts[1].Trim().ToUpperInvariant() : "";
        }

        private static bool MatchesPrefix(string model, string[] prefixes)
        {
            if (string.IsNullOrEmpty(model)) return false;
            foreach (var p in prefixes) if (model.StartsWith(p)) return true;
            return false;
        }

        private static bool ResourceHasAnyPid(string resource, string[] pids)
        {
            string r = (resource ?? "").ToUpperInvariant();
            foreach (var p in pids) if (r.Contains(p)) return true;
            return false;
        }

        private static bool IsDcLoadDevice(string resource, string idn)
        {
            return MatchesPrefix(InstrModel(idn), DcLoadModelPrefixes)
                || ResourceHasAnyPid(resource, DcLoadUsbPids);
        }

        private static bool IsPowerMeterDevice(string resource, string idn)
        {
            return MatchesPrefix(InstrModel(idn), PowerMeterModelPrefixes)
                || (idn ?? "").ToUpperInvariant().Contains("YOKOGAWA")
                || ResourceHasAnyPid(resource, PowerMeterUsbPids);
        }

        private static MeterVendor DetectVendor(string idn)
        {
            if (string.IsNullOrEmpty(idn)) return MeterVendor.Unknown;
            string u = idn.ToUpperInvariant();
            if (u.Contains("CHROMA")) return MeterVendor.Chroma;
            if (u.Contains("YOKOGAWA")) return MeterVendor.Yokogawa;
            return MeterVendor.Unknown;
        }

        private string QueryIdn(string resource)
        {
            try
            {
                using (var s = OpenSession(resource, ProbeTimeoutMs))
                {
                    string idn = Query(s, "*IDN?");
                    // 掃描只問一句 *IDN?，但這一句就足以讓機器進入遠端模式並鎖住前面板。
                    // 掃描後不見得會接著連線，因此問完就切回本機。
                    ReturnToLocal(s, DetectVendor(idn));
                    return idn;
                }
            }
            catch { return null; }
        }

        private void ScanVisaDevices()
        {
            if (_rm == null) { SetStatus("NI-VISA 未就緒，無法掃描。"); return; }

            string prevMeter = AddressOf(cbbMeter);
            string prevLoad = AddressOf(cbbLoad);

            cbbMeter.Items.Clear();
            cbbLoad.Items.Clear();

            // 「確定認出型別」的裝置另外記一份。一鍵連線只會自動連這些，
            // 認不出型別的裝置雖然兩個下拉都會列出，但不會被誤當成兩台儀器同時連。
            var identifiedMeters = new List<string>();
            var identifiedLoads = new List<string>();

            try
            {
                var resources = _rm.Find("USB?*INSTR");
                int n = 0;
                foreach (string resource in resources)
                {
                    n++;
                    string idn = QueryIdn(resource);
                    string display = resource;
                    if (!string.IsNullOrEmpty(idn))
                    {
                        var parts = idn.Split(',');
                        if (parts.Length >= 2)
                            display = $"{parts[0].Trim()} {parts[1].Trim()} ({resource})";
                    }
                    var dev = new VisaDevice(resource, display);

                    bool isLoad = IsDcLoadDevice(resource, idn);
                    bool isMeter = IsPowerMeterDevice(resource, idn);
                    if (isMeter) identifiedMeters.Add(resource);
                    if (isLoad) identifiedLoads.Add(resource);

                    // 認不出來的裝置兩邊都列出，讓使用者自行判斷，不要直接遺失。
                    if (!isLoad && !isMeter) { isLoad = true; isMeter = true; }

                    if (isMeter) cbbMeter.Items.Add(dev);
                    if (isLoad) cbbLoad.Items.Add(dev);

                    Log($"找到 {resource}  →  {idn ?? "(無 *IDN? 回應)"}");
                }
                SetStatus($"VISA 掃描完成，共 {n} 台裝置"
                        + $"（功率計 {identifiedMeters.Count}、DC Load {identifiedLoads.Count}）。");
            }
            catch (Exception ex) when (ex is DllNotFoundException
                                    || ex is TypeInitializationException
                                    || ex is BadImageFormatException)
            {
                SetStatus("NI-VISA driver 未安裝，USB 掃描已略過。");
                Log("NI-VISA 無法使用：" + ex.Message);
            }
            catch (Exception ex)
            {
                SetStatus("找不到任何 USB VISA 裝置。");
                Log("VISA 掃描：" + ex.Message);
            }

            // 記住這次確定認出的裝置，供一鍵連線判斷該連幾台。
            _identifiedMeters = identifiedMeters;
            _identifiedLoads = identifiedLoads;

            RestoreSelection(cbbMeter, prevMeter, identifiedMeters);
            RestoreSelection(cbbLoad, prevLoad, identifiedLoads);
            UpdateUiState();
        }

        private static string AddressOf(ComboBox cbb)
        {
            var dev = cbb.SelectedItem as VisaDevice;
            return dev != null ? dev.Address : cbb.Text.Trim();
        }

        /// <summary>
        /// 掃描後的選取順序：上次選的（若這次仍在）→ 這次確定認出型別的第一台 → 清單第一筆。
        /// </summary>
        private static void RestoreSelection(ComboBox cbb, string address, List<string> preferred)
        {
            if (!string.IsNullOrEmpty(address))
            {
                var keep = cbb.Items.Cast<VisaDevice>().FirstOrDefault(d => d.Address == address);
                if (keep != null) { cbb.SelectedItem = keep; return; }
            }
            foreach (var addr in preferred)
            {
                var m = cbb.Items.Cast<VisaDevice>().FirstOrDefault(d => d.Address == addr);
                if (m != null) { cbb.SelectedItem = m; return; }
            }
            if (cbb.Items.Count > 0) cbb.SelectedIndex = 0;
        }

        #endregion

        #region 能力探測

        /// <summary>
        /// 連線時對機器做一次能力探測，得出「這台機器實際支援哪些參數」。
        /// 不依賴型號硬編碼，因此 66202 / 66205 / WT210 / WT310 都走同一條路。
        /// </summary>
        private MeterProfile ProbeMeter(string address)
        {
            var p = new MeterProfile { Address = address };
            MessageBasedSession s = OpenSession(address, ProbeTimeoutMs);
            bool probed = false;
            try
            {
                p.Idn = Query(s, "*IDN?");
                p.Vendor = DetectVendor(p.Idn);
                p.Model = InstrModel(p.Idn);
                Log($"*IDN? → {p.Idn}");

                if (p.Vendor == MeterVendor.Chroma)
                {
                    ProbeChroma(ref s, p);
                    // Chroma 沒有獨立的積分歸零命令：TRIGger OFF 本身就是「停止並重置」，
                    // 每一輪的 TRIGger ON 都從零開始累加。
                    p.EnergyResetCommand = null;
                }
                else if (p.Vendor == MeterVendor.Yokogawa)
                {
                    ProbeYokogawa(ref s, p);
                    if (p.SupportsEnergy) p.EnergyResetCommand = ":INTEGrate:RESet";
                }
                else throw new Exception("無法辨識的功率計廠商（*IDN?: " + p.Idn + "）");

                if (p.SupportsEnergy)
                {
                    Log(p.EnergyResetCommand != null
                        ? "    積分歸零命令：" + p.EnergyResetCommand
                        : "    積分歸零：以 TRIGger OFF 停止並重置，下次 TRIGger ON 從零重新累加。");
                }
                probed = true;
            }
            finally
            {
                // 只有探測失敗才切回本機：成功的話呼叫端隨即開正式連線，機器本來就該留在遠端。
                if (!probed) { try { ReturnToLocal(s, p.Vendor); } catch { } }
                try { s?.Dispose(); } catch { }
            }
            return p;
        }

        private void ProbeChroma(ref MessageBasedSession s, MeterProfile p)
        {
            // 快路徑：先試一次七項複合查詢。成功就代表全部支援，省下逐項探測的時間。
            string full = "FETCh? " + string.Join(",", AllQuantities.Select(q => ChromaKeyword[q]));
            try
            {
                var vals = ParseList(Query(s, full));
                if (vals.Count >= AllQuantities.Length && vals.Any(v => v.HasValue))
                {
                    p.Supported.AddRange(AllQuantities);
                    p.CompositeOk = true;
                    Log("Chroma 複合查詢一次通過，七項參數全部支援。");
                    return;
                }
            }
            catch { /* 有不支援的關鍵字 → 整條命令失敗，改走逐項探測 */ }

            ReopenSession(ref s, p.Address, ProbeTimeoutMs);
            Log("複合查詢失敗，改為逐項探測…");

            // 慢路徑：逐項確認。無回應（逾時）或 SYSTem:ERRor? 非 0 都視為不支援。
            foreach (var q in AllQuantities)
            {
                string kw = ChromaKeyword[q];
                bool ok = false;
                try
                {
                    TryWrite(s, "*CLS");
                    if (ParseNumber(Query(s, "FETCh? " + kw)).HasValue)
                        ok = !HasScpiError(s);
                }
                catch { ReopenSession(ref s, p.Address, ProbeTimeoutMs); }

                if (ok) p.Supported.Add(q);
                Log($"    {kw,-5} → {(ok ? "支援" : "不支援")}");
            }

            // 支援項的子集再試一次複合查詢，可用就用（一次來回最省時間）。
            if (p.Supported.Count > 1)
            {
                try
                {
                    string cmd = "FETCh? " + string.Join(",", p.Supported.Select(q => ChromaKeyword[q]));
                    var vals = ParseList(Query(s, cmd));
                    p.CompositeOk = vals.Count >= p.Supported.Count && vals.Any(v => v.HasValue);
                }
                catch
                {
                    p.CompositeOk = false;
                    ReopenSession(ref s, p.Address, ProbeTimeoutMs);
                }
            }
        }

        private void ProbeYokogawa(ref MessageBasedSession s, MeterProfile p)
        {
            TryWrite(s, ":COMMunicate:HEADer OFF");   // 回應不帶命令標頭，解析較單純
            TryWrite(s, "*CLS");

            int slot = 0;   // 已確認可用的項目數，同時也是下一個要佔用的 ITEM 編號 - 1
            foreach (var q in AllQuantities)
            {
                string resolved = null;
                foreach (string cand in YokogawaItems[q])
                {
                    try
                    {
                        Write(s, $":NUMeric:NORMal:ITEM{slot + 1} {cand},1");
                        // 讀回設定值確認機器真的接受了這個名稱。
                        // 名稱不支援時機器只會記錄錯誤、不會改寫 ITEM，讀回的仍是舊值。
                        if (!ItemNameMatches(Query(s, $":NUMeric:NORMal:ITEM{slot + 1}?"), cand)) continue;

                        if (Sanitize(ParseNumber(Query(s, $":NUMeric:NORMal:VALue? {slot + 1}"))).HasValue)
                        {
                            resolved = cand;
                            break;
                        }
                    }
                    catch
                    {
                        ReopenSession(ref s, p.Address, ProbeTimeoutMs);
                        TryWrite(s, ":COMMunicate:HEADer OFF");
                    }
                }

                if (resolved != null)
                {
                    p.Supported.Add(q);
                    p.ItemName[q] = resolved;
                    slot++;
                }
                Log($"    {Abbrev(q),-5} → {(resolved ?? "不支援")}");
            }

            // 把 NUMBer 設成支援項目數，讓一次 :NUMeric:NORMal:VALue? 就取回全部欄位。
            ApplyYokogawaItems(s, p);
            try
            {
                var vals = ParseList(Query(s, ":NUMeric:NORMal:VALue?"));
                p.CompositeOk = vals.Count >= p.Supported.Count && p.Supported.Count > 0;
            }
            catch
            {
                p.CompositeOk = false;
                ReopenSession(ref s, p.Address, ProbeTimeoutMs);
            }
        }

        private static void ApplyYokogawaItems(MessageBasedSession s, MeterProfile p)
        {
            for (int i = 0; i < p.Supported.Count; i++)
                TryWrite(s, $":NUMeric:NORMal:ITEM{i + 1} {p.ItemName[p.Supported[i]]},1");
            TryWrite(s, $":NUMeric:NORMal:NUMBer {p.Supported.Count}");
        }

        /// <summary>比對 ITEM 讀回值是否就是我們要設定的名稱（容許機器回傳更完整的正式名稱）。</summary>
        private static bool ItemNameMatches(string response, string expected)
        {
            if (string.IsNullOrEmpty(response)) return false;
            string t = response.Trim();
            // 若 HEADer 仍為 ON，回應會像 ":NUMERIC:NORMAL:ITEM1 U,1"，取空白後那一段。
            if (t.Contains(":"))
            {
                int sp = t.LastIndexOf(' ');
                if (sp >= 0) t = t.Substring(sp + 1);
            }
            string name = t.Split(',')[0].Trim().Trim('"').ToUpperInvariant();
            string exp = expected.ToUpperInvariant();
            return name == exp || name.StartsWith(exp);
        }

        #endregion

        #region 讀取量測值

        /// <summary>讀取所有支援的量測值。阻塞式，必須在背景執行緒且已取得 _meterIo 的情況下呼叫。</summary>
        private Dictionary<Quantity, double?> ReadAll()
        {
            var result = new Dictionary<Quantity, double?>();
            foreach (var q in AllQuantities) result[q] = null;

            var p = _profile;
            if (p == null || _meter == null || p.Supported.Count == 0) return result;

            if (p.Vendor == MeterVendor.Chroma) ReadChroma(p, result);
            else ReadYokogawa(p, result);
            return result;
        }

        private void ReadChroma(MeterProfile p, Dictionary<Quantity, double?> result)
        {
            if (p.CompositeOk)
            {
                string cmd = "FETCh? " + string.Join(",", p.Supported.Select(q => ChromaKeyword[q]));
                var vals = ParseList(Query(_meter, cmd));
                if (vals.Count >= p.Supported.Count)
                {
                    for (int i = 0; i < p.Supported.Count; i++) result[p.Supported[i]] = vals[i];
                    return;
                }
                // 欄位數對不上就不要硬套，改走逐項查詢並記住。
                Log($"複合查詢回應欄位數不符（{vals.Count}/{p.Supported.Count}），改為逐項查詢。");
                p.CompositeOk = false;
            }

            foreach (var q in p.Supported)
                result[q] = ParseNumber(Query(_meter, "FETCh? " + ChromaKeyword[q]));
        }

        private void ReadYokogawa(MeterProfile p, Dictionary<Quantity, double?> result)
        {
            if (p.CompositeOk)
            {
                var vals = ParseList(Query(_meter, ":NUMeric:NORMal:VALue?"));
                if (vals.Count >= p.Supported.Count)
                {
                    for (int i = 0; i < p.Supported.Count; i++) result[p.Supported[i]] = Sanitize(vals[i]);
                    return;
                }
                Log($"NUMeric 複合查詢欄位數不符（{vals.Count}/{p.Supported.Count}），改為逐項查詢。");
                p.CompositeOk = false;
            }

            for (int i = 0; i < p.Supported.Count; i++)
                result[p.Supported[i]] = Sanitize(ParseNumber(Query(_meter, $":NUMeric:NORMal:VALue? {i + 1}")));
        }

        /// <summary>
        /// 只讀指定的幾個量測項（積分期間取樣 VTHD / ITHD 用），盡量壓在一次來回內完成。
        /// 阻塞式，必須在背景執行緒且已取得 _meterIo 的情況下呼叫。
        /// </summary>
        private Dictionary<Quantity, double?> ReadSubset(MeterProfile p, Quantity[] wanted)
        {
            var result = new Dictionary<Quantity, double?>();
            foreach (var q in wanted) result[q] = null;

            if (p == null || _meter == null) return result;
            var items = wanted.Where(q => p.Supported.Contains(q)).ToList();
            if (items.Count == 0) return result;

            if (p.Vendor == MeterVendor.Chroma)
            {
                if (items.Count > 1 && p.CompositeOk)
                {
                    string cmd = "FETCh? " + string.Join(",", items.Select(q => ChromaKeyword[q]));
                    var vals = ParseList(Query(_meter, cmd));
                    if (vals.Count >= items.Count)
                    {
                        for (int i = 0; i < items.Count; i++) result[items[i]] = vals[i];
                        return result;
                    }
                }
                foreach (var q in items)
                    result[q] = ParseNumber(Query(_meter, "FETCh? " + ChromaKeyword[q]));
            }
            else
            {
                // Yokogawa 的複合查詢一次就回傳所有 ITEM，比逐槽查還省來回，取回後挑需要的欄位。
                if (p.CompositeOk)
                {
                    var vals = ParseList(Query(_meter, ":NUMeric:NORMal:VALue?"));
                    if (vals.Count >= p.Supported.Count)
                    {
                        foreach (var q in items) result[q] = Sanitize(vals[p.Supported.IndexOf(q)]);
                        return result;
                    }
                }
                foreach (var q in items)
                {
                    int slot = p.Supported.IndexOf(q) + 1;
                    result[q] = Sanitize(ParseNumber(Query(_meter, $":NUMeric:NORMal:VALue? {slot}")));
                }
            }
            return result;
        }

        /// <summary>單獨讀取積分電能。不支援 WH 的機器一律回傳 null。</summary>
        private double? ReadEnergy(MeterProfile p)
        {
            if (p == null || _meter == null || !p.SupportsEnergy) return null;

            if (p.Vendor == MeterVendor.Chroma)
                return ParseNumber(Query(_meter, "FETCh? WH"));

            int slot = p.Supported.IndexOf(Quantity.WattHour) + 1;
            return Sanitize(ParseNumber(Query(_meter, $":NUMeric:NORMal:VALue? {slot}")));
        }

        #endregion

        #region DC Load 端讀值與效率

        /// <summary>
        /// 讀取拉載中的負載端 V / I / W。
        /// 阻塞式，必須在背景執行緒且已取得 _loadIo 的情況下呼叫。
        ///
        /// useMeasure = false → FETCh：直接取量測緩衝區的現值，最快。
        /// useMeasure = true  → MEASure：先觸發一次新的擷取再回傳，較慢但保證是當下重新量到的。
        /// </summary>
        private LoadReading ReadLoadValues(LoadProfile lp, bool useMeasure)
        {
            var r = new LoadReading();
            if (_load == null || lp == null || lp.Commands == null) return r;

            var cs = lp.Commands;
            if (lp.CanReadVoltage)
                r.Voltage = Magnitude(ParseNumber(Query(_load, useMeasure ? cs.MeasVoltage : cs.FetchVoltage)));
            if (lp.CanReadCurrent)
                r.Current = Magnitude(ParseNumber(Query(_load, useMeasure ? cs.MeasCurrent : cs.FetchCurrent)));

            if (lp.CanReadPower)
                r.Watt = Magnitude(ParseNumber(Query(_load, useMeasure ? cs.MeasPower : cs.FetchPower)));
            else if (r.Voltage.HasValue && r.Current.HasValue)
            {
                // 機器不回傳功率時用 V × I 補上。DC 負載沒有相位問題，兩者相乘就是實功率。
                r.Watt = r.Voltage.Value * r.Current.Value;
                r.WattComputed = true;
            }
            return r;
        }

        /// <summary>
        /// 拉載時電流與功率帶負號（表示逆灌 / 汲取），取絕對值換成「負載吃掉多少」。
        /// 一併沿用 Sanitize 濾掉 NaN / Inf 與機器的「無資料」代碼。
        /// </summary>
        private static double? Magnitude(double? v)
        {
            var s = Sanitize(v);
            return s.HasValue ? (double?)Math.Abs(s.Value) : null;
        }

        /// <summary>
        /// 讀一次負載端數值。負載讀值是輔助資訊，失敗不該讓掃描中斷，
        /// 因此除了取消以外的例外都吞掉並只提示一次，回傳 null 代表這次沒讀到。
        /// </summary>
        private async Task<LoadReading> ReadLoadAsync(CancellationToken ct)
        {
            var lp = _loadProfile;
            if (_load == null || lp == null || !lp.ReadbackOk) return null;

            try
            {
                return await LoadExecAsync(() => ReadLoadValues(lp, _loadUseMeasure), ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (!_loadReadErrorLogged)
                {
                    _loadReadErrorLogged = true;
                    Log("DC Load 讀值失敗（本次流程後續不再重複提示）：" + ex.Message);
                }
                return null;
            }
        }

        /// <summary>
        /// 效率 = 負載端功率（待測物輸出）÷ 功率計功率（待測物輸入）× 100%。
        ///
        /// 輸入功率接近 0 時（未上電、或 0% 負載點的待機功耗被量成 0）相除沒有意義，
        /// 一律回傳 null 顯示 N/A，而不是吐出一個看起來像數字的天文數字。
        /// </summary>
        private static double? CalcEfficiency(double? meterWatt, double? loadWatt)
        {
            if (!meterWatt.HasValue || !loadWatt.HasValue) return null;
            double inWatt = Math.Abs(meterWatt.Value);
            if (inWatt < MinEfficiencyInputWatt) return null;
            return Math.Abs(loadWatt.Value) / inWatt * 100.0;
        }

        /// <summary>
        /// 讀一次功率計＋負載端並更新畫面（不寫進 Result 表）。
        /// 用於掃描的等待期間，讓長時間的穩定 / 讀值階段也看得到即時數值。
        /// 純顯示用途，任何失敗都不影響掃描流程。
        /// </summary>
        private async Task RefreshLiveReadoutsAsync(CancellationToken ct)
        {
            try
            {
                var vals = await MeterExecAsync(() => ReadAll(), ct);
                ShowValues(vals);

                var load = await ReadLoadAsync(ct);
                if (load != null) ShowLoadValues(load, CalcEfficiency(vals[Quantity.Watt], load.Watt));
            }
            catch (OperationCanceledException) { throw; }
            catch { /* 顯示用的讀值失敗就跳過這一次，下一秒再試 */ }
        }

        #endregion

        #region 積分（Wh）流程

        private void StartIntegration(MeterProfile p, int seconds)
        {
            if (p.Vendor == MeterVendor.Chroma)
            {
                // Chroma 66200 系列手冊的積分流程：
                //   1. TRIG OFF                 停止並重置上一輪，回到等待觸發的狀態
                //   2. INTEG:TIME n             設定積分秒數
                //   3. TRIG:MODE INTEGRATION    指定 TRIGger 要觸發的功能是「積分」
                //   4. TRIG ON                  開始倒數並累加
                // 收尾在 StopIntegration()：先 FETC? WH 讀值，再 TRIG OFF 重置。
                //
                // 第 3 步不能省。TRIGger 是共用的觸發入口，沒有先指定模式，
                // TRIG ON 不會啟動積分，接著查 TRIGger? 會立刻回 STOP ——
                // 外觀上就是「按下 Start Integration 幾秒鐘就吐出結果」。
                TryWrite(_meter, "TRIGger OFF");
                TryWrite(_meter, "*CLS");

                Write(_meter, "INTEGrate:TIME " + seconds.ToString(CultureInfo.InvariantCulture));
                ResolveChromaTriggerMode(p);
                Write(_meter, "TRIGger ON");
                CheckScpiError(_meter, "功率計", "啟動積分（TRIGger ON）");
            }
            else
            {
                // Yokogawa WT 系列的積分是獨立的 :INTEGrate 子系統，
                // 且必須先 STOP + RESet 才允許變更模式與計時器設定。
                TryWrite(_meter, ":INTEGrate:STOP");
                if (!string.IsNullOrEmpty(p.EnergyResetCommand)) TryWrite(_meter, p.EnergyResetCommand);
                Write(_meter, ":INTEGrate:MODE NORMal");
                int h = seconds / 3600, m = (seconds % 3600) / 60, sec = seconds % 60;
                Write(_meter, $":INTEGrate:TIMer {h},{m},{sec}");
                Write(_meter, ":INTEGrate:STARt");
            }
        }

        /// <summary>
        /// 決定並送出 Chroma 的積分觸發模式命令。不同機種／韌體的參數名不一致
        /// （INTEGRATION vs ENERGY），第一次逐一試出可用的那個就記在 profile 裡沿用。
        /// </summary>
        private void ResolveChromaTriggerMode(MeterProfile p)
        {
            if (p.TriggerModeCommand != null)
            {
                Write(_meter, p.TriggerModeCommand);
                return;
            }

            foreach (string cand in ChromaTriggerModes)
            {
                if (!TryCommand(_meter, cand)) continue;
                p.TriggerModeCommand = cand;
                Log("積分觸發模式命令：" + cand);
                return;
            }

            // 全部被拒仍照送第一個，讓後續的錯誤佇列檢查與狀態輪詢把問題暴露出來，
            // 而不是靜悄悄地跳過這一步。
            p.TriggerModeCommand = ChromaTriggerModes[0];
            Write(_meter, p.TriggerModeCommand);
            Log($"※ 觸發模式命令全數被拒（已試 {string.Join(" / ", ChromaTriggerModes)}），"
              + "積分可能無法啟動，請確認機器韌體版本。");
        }

        private string QueryIntegrationState(MeterProfile p)
        {
            string raw = p.Vendor == MeterVendor.Chroma
                ? Query(_meter, "TRIGger?")
                : Query(_meter, ":INTEGrate:STATe?");
            return (raw ?? "").Trim().Trim('"').ToUpperInvariant();
        }

        /// <summary>
        /// 判斷倒數是否結束。各機種回傳字串寫法不一（RUNNING/RUN/STARt/STOP/FINISH/TIMEUP），
        /// 因此用「包含」比對而非等號比對。
        /// </summary>
        private static bool IsIntegrationDone(string state)
        {
            if (string.IsNullOrEmpty(state)) return false;
            if (IsIntegrationRunning(state)) return false;
            return state.Contains("STOP") || state.Contains("FINISH")
                || state.Contains("TIMEUP") || state.Contains("TIMUP") || state.Contains("ERR")
                // Chroma 的 TRIGger? 只回 ON / OFF（部分韌體回 1 / 0），沒有專屬的「倒數結束」字樣。
                || state == "OFF" || state == "0";
        }

        /// <summary>倒數是否仍在進行。與 IsIntegrationDone 互斥，兩者都不成立時視為「狀態不明」。</summary>
        private static bool IsIntegrationRunning(string state)
        {
            if (string.IsNullOrEmpty(state)) return false;
            if (state.Contains("ERR")) return false;
            return state.Contains("RUN") || state.Contains("STAR")
                || state == "ON" || state == "1";
        }

        private void StopIntegration(MeterProfile p)
        {
            if (p.Vendor == MeterVendor.Chroma)
            {
                // 手冊流程的最後一步：停止並重置積分狀態，回到等待下次 TRIG ON 的狀態。
                // 呼叫端已在此之前讀完 FETC? WH —— 重置會把累積值清掉。
                TryWrite(_meter, "TRIGger OFF");
            }
            else
            {
                TryWrite(_meter, ":INTEGrate:STOP");
            }
        }

        /// <summary>
        /// 跑完一次積分並回傳當下所有量測值（含 Wh）。
        /// 刻意在「機器已停止倒數、但積分尚未關閉」的時間點讀值 ——
        /// 這時候的 Wh 才是這一輪準確的積分結果，關閉積分可能會把累積值清掉。
        /// 回傳 null 表示中途被取消。
        /// </summary>
        private async Task<IntegrationResult> RunIntegrationCycleAsync(int seconds, CancellationToken ct)
        {
            var p = _profile;
            var avg = new Averager();
            bool sampleErrorLogged = false;

            SetIntegState("STARTING");
            SetIntegResult(null);

            // 觸發的同時取第一筆 THD，之後在每次輪詢時繼續取樣。
            await MeterRunAsync(() =>
            {
                StartIntegration(p, seconds);
                SampleThd(p, avg, ref sampleErrorLogged);
            }, ct);
            Log($"積分開始：倒數 {seconds} 秒，期間持續取樣 VTHD / ITHD。");

            var sw = Stopwatch.StartNew();
            double limit = seconds + PollGraceSeconds;   // 輪詢逾時上限，避免機器沒進入 STOP 就無限等待
            bool finished = false;
            bool sawRunning = false;
            bool stateLogged = false;
            string state = "";

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(PollIntervalMs, ct);

                // 設定秒數還沒到，就一律當成「積分進行中」，不理會機器回報的狀態。
                // 各機種的狀態字串寫法不一，本工具判讀不出來（或積分根本沒被啟動）時，
                // 若照單全收就會在第一次輪詢直接結束、幾秒鐘就吐出結果。
                bool withinWindow = sw.Elapsed.TotalSeconds < seconds;

                // 一次進鎖同時做兩件事：查倒數狀態、取樣 THD。減少鎖競爭也避免多餘的來回。
                bool errFlag = sampleErrorLogged;
                state = await MeterExecAsync(() =>
                {
                    string st = QueryIntegrationState(p);
                    // 時間到了之後才看狀態：已經停止就不再取樣，停止後的值不屬於這一輪積分區間。
                    if (withinWindow || !IsIntegrationDone(st)) SampleThd(p, avg, ref errFlag);
                    return st;
                }, ct);
                sampleErrorLogged = errFlag;

                if (!stateLogged)
                {
                    stateLogged = true;
                    Log($"積分狀態查詢首次回應：\"{state}\""
                      + $"（判讀為 {(IsIntegrationRunning(state) ? "執行中" : IsIntegrationDone(state) ? "已停止" : "無法判讀")}）。");
                }
                if (IsIntegrationRunning(state)) sawRunning = true;

                UpdateIntegProgress(state, sw.Elapsed.TotalSeconds, seconds);

                if (!withinWindow && IsIntegrationDone(state)) { finished = true; break; }

                if (sw.Elapsed.TotalSeconds > limit)
                {
                    Log($"積分輪詢逾時：已等待 {sw.Elapsed.TotalSeconds:F0} 秒（上限 {limit:F0} 秒），"
                      + $"最後狀態 = \"{state}\"。中止並嘗試讀取現值。");
                    break;
                }
            }

            if (ct.IsCancellationRequested)
            {
                await MeterRunAsync(() => StopIntegration(p), CancellationToken.None);
                SetIntegState("CANCELLED");
                Log("積分已由使用者中止。");
                return null;
            }

            var vals = await MeterExecAsync(() => ReadAll(), ct);
            await MeterRunAsync(() => StopIntegration(p), CancellationToken.None);

            // WH 以停止當下的讀值為準；VTHD / ITHD 換成積分期間的平均值。
            // 取樣一筆都沒成功時保留最後的瞬時讀值，總比留空好。
            int samples = 0;
            foreach (var q in ThdQuantities)
            {
                double? mean = avg.Mean(q);
                if (mean.HasValue) vals[q] = mean;
                samples = Math.Max(samples, avg.Samples(q));
            }

            SetIntegState(finished ? state : "TIMEOUT");
            SetIntegResult(vals[Quantity.WattHour]);
            ShowValues(vals);

            if (!vals[Quantity.WattHour].HasValue)
            {
                // 防呆：機器停了但讀不到 Wh，明確告知而不是把 0 當成結果。
                Log("積分結束但讀不到 Wh 數值（可能量測期間未真正進入積分模式）。");
            }
            if (!sawRunning)
                Log("積分期間狀態從未回報「執行中」，機器可能沒有真的進入積分模式，Wh 請以面板數值核對。");
            if (samples == 0 && ThdQuantities.Any(q => p.Supported.Contains(q)))
                Log("積分期間未取得任何 VTHD / ITHD 取樣，Result 中的 THD 為結束當下的瞬時值。");

            return new IntegrationResult { Values = vals, ThdSamples = samples };
        }

        /// <summary>
        /// 取一筆 VTHD / ITHD 樣本。THD 只是輔助資料，取樣失敗不該讓整輪積分（甚至整趟掃描）中斷，
        /// 所以這裡把例外吞掉並只記錄一次；連線若真的斷了，同一個鎖區間內的狀態輪詢會先丟出例外。
        /// </summary>
        private void SampleThd(MeterProfile p, Averager avg, ref bool errorLogged)
        {
            try
            {
                var thd = ReadSubset(p, ThdQuantities);
                foreach (var q in ThdQuantities) avg.Add(q, thd[q]);
            }
            catch (Exception ex)
            {
                if (!errorLogged)
                {
                    errorLogged = true;
                    Log("THD 取樣失敗（本輪後續不再重複提示）：" + ex.Message);
                }
            }
        }

        #endregion

        #region DC Load 掃描

        /// <summary>依步進百分比產生負載點：0%、step%、2×step% … 100%（最後一點一定是 100%）。</summary>
        private static List<int> BuildSweepPoints(int stepPct)
        {
            var points = new List<int> { 0 };
            for (int v = stepPct; v < 100; v += stepPct) points.Add(v);
            points.Add(100);
            return points;
        }

        /// <summary>
        /// 負載掃描主流程，兩種模式共用一份程式碼，差別只在每個負載點內的時間怎麼分：
        ///
        ///   Start Sweep（integSeconds = 0）純負載掃描
        ///     設定電流 ── 停留 Delay 秒 ── 讀值寫一行 ── 下一點
        ///
        ///   Start Integration（integSeconds &gt; 0）含積分的負載掃描
        ///     設定電流 ── 穩定 Delay 秒 ── 積分 Count 秒 ── 讀值寫一行 ── 下一點
        ///     其中穩定期的前 5 秒（SweepPointBufferSeconds）刻意不刷新畫面，
        ///     讓上一點的讀值停著可以核對 —— 電流已經換過去，時間也照跑，
        ///     所以 Delay 10 秒的話，讀值看完剛好還剩 5 秒就要開始積分。
        ///
        /// Delay 與 Count 是兩個獨立的時間，互不侵蝕：
        /// Delay 只管「換載之後等多久才開始取數據」，Count 只管「積分要跑多久」。
        /// 純掃描每點 = Delay；積分掃描每點 = Delay + Count，設定多久就跑多久。
        /// </summary>
        private async Task RunSweepAsync(double maxCurrent, int stepPct, int delaySec, int integSeconds,
                                         CancellationToken ct)
        {
            bool withInteg = integSeconds > 0;
            // Delay 一律是完整的穩定時間，不因為要積分而被扣掉。
            int settleSec = delaySec;
            int pointSec = delaySec + (withInteg ? integSeconds : 0);
            // 保留上一點讀值的秒數。與穩定期重疊，Delay 太短時就只保留到穩定期結束。
            int holdSec = withInteg ? Math.Min(SweepPointBufferSeconds, settleSec) : 0;

            var points = BuildSweepPoints(stepPct);
            Log(withInteg
                ? $"開始積分掃描：最大電流 {maxCurrent:F2} A、每階 {stepPct}%、共 {points.Count} 點；"
                  + $"每點 {pointSec}s（穩定 {settleSec}s + 積分 {integSeconds}s，"
                  + $"穩定期前 {holdSec}s 保留上一點讀值）。"
                : $"開始掃描：最大電流 {maxCurrent:F2} A、每階 {stepPct}%、共 {points.Count} 點，"
                  + $"每點停留 {delaySec}s。");

            var cs = _loadProfile != null ? _loadProfile.Commands : LoadCommandSets[0];
            _loadReadErrorLogged = false;

            await LoadRunAsync(() =>
            {
                TryWrite(_load, "*CLS");
                // 62000D 這類雙向電源開機時可能停在 Source 模式，不先切到 Load 模式，
                // 後面的 LOAD:CURRent 會被機器忽略。63xxx 電子負載沒有這道手續。
                if (cs.EnterLoadMode != null)
                {
                    Write(_load, cs.EnterLoadMode);
                    Log("DC Load 切換為負載模式：" + cs.EnterLoadMode);
                }
                Write(_load, SetCurrentCommand(0));
                Write(_load, cs.OutputOn);
                CheckLoadError("開啟負載（" + cs.OutputOn + "）");
            }, ct);
            Log($"DC Load 已開啟，使用命令族：{cs.Name}。");

            try
            {
                double? prevWh = null;
                string prevTag = null;
                for (int i = 0; i < points.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    int pct = points[i];
                    double iset = Math.Round(maxCurrent * pct / 100.0, 2);
                    string cmd = SetCurrentCommand(iset);
                    await LoadRunAsync(() =>
                    {
                        Write(_load, cmd);
                        // 每點都確認一次：電流超出機器的 LIMit 設定時，命令會被拒絕但外觀毫無異狀。
                        CheckLoadError(cmd);
                    }, ct);

                    string tag = $"[{i + 1}/{points.Count}] {pct}% ({iset:F2} A)";

                    // 電流下完先讓負載與待測物穩定，之後才積分／讀值 ——
                    // 取到的才是這個負載點穩態下的數值。
                    //
                    // 積分模式下，穩定期的前幾秒刻意不刷新畫面，把上一點的讀值留著給人核對。
                    // 電流早就換過去、時間也照跑，所以這段不會延後積分，只是「晚幾秒才開始看新值」。
                    int hold = (prevTag != null) ? holdSec : 0;
                    if (hold > 0) await CountdownAsync(hold, prevTag + " 讀值中", ct);

                    // 其餘的穩定時間持續刷新功率計與負載讀值，看得出待測物是否已經穩定。
                    await CountdownAsync(settleSec - hold, tag + (withInteg ? " 穩定中" : " 停留中"), ct, true);

                    Dictionary<Quantity, double?> vals;
                    int thdSamples = 0;
                    if (withInteg)
                    {
                        SetStatus(tag + " 積分中…");
                        var res = await RunIntegrationCycleAsync(integSeconds, ct);
                        if (res == null) break;   // 已被取消
                        vals = res.Values;
                        thdSamples = res.ThdSamples;
                    }
                    else
                    {
                        vals = await MeterExecAsync(() => ReadAll(), ct);
                        ShowValues(vals);
                    }

                    // 功率計讀完緊接著讀負載端，兩邊的取樣時間才靠得夠近，
                    // 相除得到的效率才對得起同一個工作點。
                    var load = await ReadLoadAsync(ct);
                    double? eff = load != null ? CalcEfficiency(vals[Quantity.Watt], load.Watt) : null;
                    if (load != null) ShowLoadValues(load, eff);

                    // ΔWh：與上一點的差值。積分模式下每點都從零重新累加，這一欄通常與 WH 相同；
                    // 若儀器改成跨點累加，這一欄才是該點實際消耗的能量。
                    double? wh = vals[Quantity.WattHour];
                    double? deltaWh = (wh.HasValue && prevWh.HasValue) ? wh - prevWh : null;
                    if (wh.HasValue) prevWh = wh;

                    AddResultRow(pct + "%", iset, vals, load, eff, deltaWh, thdSamples);
                    Log(tag + " 完成" + FormatEfficiencyNote(vals[Quantity.Watt], load, eff) + "。");

                    // 這一點的讀值要停在畫面上多久，由下一點的穩定期開頭決定（見上方 hold）。
                    prevTag = tag;
                }

                if (!ct.IsCancellationRequested)
                {
                    SetStatus("掃描完成，負載已關閉。");
                    Log("掃描完成。");
                }
            }
            finally
            {
                // 不論正常結束、例外或使用者中斷，一律歸零並 LOAD OFF。
                // 這裡刻意不帶取消權杖，否則中斷時連收尾都會被略過。
                try
                {
                    if (withInteg) await MeterRunAsync(() => StopIntegration(_profile), CancellationToken.None);
                    await LoadRunAsync(() =>
                    {
                        Write(_load, SetCurrentCommand(0));
                        Write(_load, cs.OutputOff);
                    }, CancellationToken.None);
                    // 掃描期間負載由這裡控制，結束後手動開關必須跟著回到 OFF。
                    _loadOutputOn = false;
                    Log($"負載已歸零並關閉（{cs.OutputOff}）。");
                }
                catch (Exception ex)
                {
                    Log("收尾關閉負載失敗：" + ex.Message);
                }
            }
        }

        /// <summary>
        /// 可中斷的等待，並在狀態列即時倒數，讓長時間的穩定等待看得出程式還活著。
        /// liveReadout = true 時，每一秒順便刷新一次功率計與負載端的讀值（只顯示，不寫進 Result）。
        /// </summary>
        private async Task CountdownAsync(int seconds, string prefix, CancellationToken ct,
                                          bool liveReadout = false)
        {
            if (seconds <= 0) return;
            var sw = Stopwatch.StartNew();
            while (true)
            {
                double remain = seconds - sw.Elapsed.TotalSeconds;
                if (remain <= 0) break;
                SetStatus($"{prefix}… 剩餘 {remain:F0} 秒");

                if (liveReadout) await RefreshLiveReadoutsAsync(ct);

                remain = seconds - sw.Elapsed.TotalSeconds;
                if (remain <= 0) break;
                await Task.Delay(Math.Max(1, (int)Math.Min(1000, remain * 1000)), ct);
            }
        }

        #endregion

        #region 背景 I/O 包裝

        private Task<T> MeterExecAsync<T>(Func<T> action, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                _meterIo.Wait(ct);
                try { return action(); }
                finally { _meterIo.Release(); }
            }, ct);
        }

        private Task MeterRunAsync(Action action, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                _meterIo.Wait(ct);
                try { action(); }
                finally { _meterIo.Release(); }
            }, ct);
        }

        private Task<T> LoadExecAsync<T>(Func<T> action, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                _loadIo.Wait(ct);
                try { return action(); }
                finally { _loadIo.Release(); }
            }, ct);
        }

        private Task LoadRunAsync(Action action, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                _loadIo.Wait(ct);
                try { action(); }
                finally { _loadIo.Release(); }
            }, ct);
        }

        #endregion

        #region 事件處理

        private void btnScanVisa_Click(object sender, EventArgs e)
        {
            ScanVisaDevices();
        }

        private void btnVisaToggle_Click(object sender, EventArgs e)
        {
            SetVisaExpanded(!_visaExpanded);
        }

        /// <summary>
        /// 展開／收合 Instruments 群組，並跟著調整視窗高度 —— 儀器選好之後這一區就用不到了，
        /// 收起來可以把視窗縮小。收合狀態下靠群組標題顯示目前連了哪些儀器。
        /// </summary>
        private void SetVisaExpanded(bool expanded)
        {
            _visaExpanded = expanded;

            foreach (Control c in gbInstruments.Controls)
                if (c != btnVisaToggle) c.Visible = expanded;

            gbInstruments.Height = expanded ? VisaExpandedHeight : VisaCollapsedHeight;
            btnVisaToggle.Text = expanded ? "▲" : "▼";
            btnVisaToggle.Location = new Point(gbInstruments.Width - 44, 0);
            ClientSize = new Size(ClientSize.Width, gbInstruments.Bottom + 8 + statusStrip1.Height);
        }

        /// <summary>
        /// 一鍵連線／中斷：偵測到幾台就連幾台，只有一台就只連一台。
        /// 型別認不出來的裝置會同時列在兩個下拉，這種情況只當成功率計連一次，
        /// 避免同一台儀器被開兩個 session 互相干擾。
        /// </summary>
        private async void btnConnectAll_Click(object sender, EventArgs e)
        {
            if (_meter != null || _load != null) { DisconnectAll(); return; }
            if (_rm == null) { Warn("NI-VISA 尚未就緒，無法連線。"); return; }

            string meterAddr = AddressOf(cbbMeter);
            string loadAddr = AddressOf(cbbLoad);
            if (!string.IsNullOrEmpty(loadAddr) && loadAddr == meterAddr)
            {
                Log("功率計與 DC Load 指向同一台裝置，只以功率計身分連線。");
                loadAddr = "";
            }

            if (string.IsNullOrEmpty(meterAddr) && string.IsNullOrEmpty(loadAddr))
            {
                Warn("沒有可連線的儀器，請先按 Scan VISA。");
                return;
            }

            btnConnectAll.Enabled = false;
            try
            {
                if (!string.IsNullOrEmpty(meterAddr)) await ConnectMeterAsync(meterAddr);
                if (!string.IsNullOrEmpty(loadAddr)) await ConnectLoadAsync(loadAddr);

                int n = (_meter != null ? 1 : 0) + (_load != null ? 1 : 0);
                SetStatus(n == 0 ? "連線失敗，沒有任何儀器連上。" : $"已連線 {n} 台儀器。");
            }
            finally
            {
                btnConnectAll.Enabled = true;
                ClearReadouts();
                UpdateUiState();
            }
        }

        private async Task ConnectMeterAsync(string address)
        {
            SetStatus("功率計連線中，正在探測支援的參數…");
            try
            {
                MeterProfile profile = await Task.Run(() => ProbeMeter(address));
                if (profile.Supported.Count == 0)
                    throw new Exception("這台機器沒有任何一項可用的量測參數，請確認機型與連線。");

                _meter = OpenSession(address, SessionTimeoutMs);
                _profile = profile;
                if (profile.Vendor == MeterVendor.Yokogawa)
                {
                    TryWrite(_meter, ":COMMunicate:HEADer OFF");
                    ApplyYokogawaItems(_meter, profile);
                }
                Log($"功率計連線成功：{profile.Model}，支援 {profile.Supported.Count} 項參數。");
            }
            catch (Exception ex)
            {
                DisposeSession(ref _meter);
                _profile = null;
                Log("功率計連線失敗：" + ex.Message);
            }
        }

        /// <summary>
        /// 連線 DC Load 並探測命令族與讀值能力。探測動作放在背景執行緒 ——
        /// 每一項不支援的查詢都要等滿一次逾時，全部串起來足以讓 UI 明顯卡住。
        /// </summary>
        private async Task ConnectLoadAsync(string address)
        {
            SetStatus("DC Load 連線中，正在探測命令族與讀值能力…");
            try
            {
                MessageBasedSession session = null;
                LoadProfile profile = null;
                await Task.Run(() =>
                {
                    // 探測期間用較短的逾時，一次查詢不被接受不必等滿 5 秒。
                    MessageBasedSession s = null;
                    try
                    {
                        s = OpenSession(address, ProbeTimeoutMs);
                        profile = ProbeLoad(ref s, address);   // 逾時重開 session 後 s 會換成新的
                        s.TimeoutMilliseconds = SessionTimeoutMs;
                        session = s;
                    }
                    catch
                    {
                        // 探測途中失敗就把 session 收乾淨，否則這條連線會一直佔著機器。
                        // 收掉之前先切回本機，免得連線失敗還把機器鎖在遠端模式。
                        try { ReturnToLocal(s, MeterVendor.Chroma); } catch { }
                        DisposeSession(ref s);
                        throw;
                    }
                });

                _load = session;
                _loadProfile = profile;
                _loadModel = profile.Model;

                Log($"DC Load 連線成功：{profile.Idn}");
                Log($"    命令族：{profile.Commands.Name}"
                  + $"（{profile.Commands.SetCurrent} / {profile.Commands.OutputOn}）"
                  + (profile.Verified ? "" : " ※ 查詢未通過，依型號推定"));
            }
            catch (Exception ex)
            {
                DisposeSession(ref _load);
                _loadProfile = null;
                _loadModel = "";
                Log("DC Load 連線失敗：" + ex.Message);
            }
        }

        /// <summary>
        /// 探測 DC Load 屬於哪一個命令族：對候選族送出它的電流查詢，
        /// 能回傳數值且錯誤佇列乾淨才算數。型號只決定嘗試順序，判定仍以機器實際回應為準。
        /// </summary>
        private LoadProfile ProbeLoad(ref MessageBasedSession s, string address)
        {
            var p = new LoadProfile();
            p.Idn = Query(s, "*IDN?");
            p.Model = InstrModel(p.Idn);

            var ordered = LoadCommandSets
                .OrderByDescending(cs => MatchesPrefix(p.Model, cs.ModelPrefixes))
                .ToList();

            foreach (var cs in ordered)
            {
                if (ProbeLoadQuery(ref s, address, cs.QueryCurrent))
                {
                    p.Commands = cs;
                    p.Verified = true;
                    break;
                }
                Log($"    {cs.QueryCurrent,-16} → 不支援");
            }

            // 查詢全部失敗時仍要給一組命令，否則整個 Sweep 無從執行。
            // 依型號推定，推不出來就用清單第一族（63xxx，本工具原本的預設）。
            if (p.Commands == null)
            {
                p.Commands = ordered[0];
                Log("    DC Load 電流命令探測失敗，依型號推定為：" + p.Commands.Name);
            }

            ProbeLoadReadback(ref s, address, p);
            return p;
        }

        /// <summary>
        /// 探測負載端的即時讀值能力（拉載中的 V / I / W）。
        /// 與功率計一樣不依型號硬編碼：機器答得出數字才算支援，
        /// 只回傳 V / I 的機種則把功率改成由 V × I 推算，不讓效率欄整欄空白。
        /// </summary>
        private void ProbeLoadReadback(ref MessageBasedSession s, string address, LoadProfile p)
        {
            var cs = p.Commands;
            p.CanReadVoltage = ProbeLoadQuery(ref s, address, cs.FetchVoltage);
            p.CanReadCurrent = ProbeLoadQuery(ref s, address, cs.FetchCurrent);
            p.CanReadPower   = ProbeLoadQuery(ref s, address, cs.FetchPower);

            Log($"    負載讀值 → V {(p.CanReadVoltage ? "支援" : "不支援")}"
              + $"、I {(p.CanReadCurrent ? "支援" : "不支援")}"
              + $"、W {(p.CanReadPower ? "支援" : p.CanComputePower ? "由 V×I 推算" : "不支援")}");

            if (!p.ReadbackOk)
                Log("    ※ 這台 DC Load 不支援 FETCh 讀值，Result 的 Load V/I/W 與效率欄將顯示 N/A。");
        }

        /// <summary>
        /// 送出一個查詢並判斷機器是否真的支援：要能解析出數字，且錯誤佇列乾淨。
        /// 逾時代表機器不認識這條命令，輸出緩衝可能殘留半筆資料，因此重開 session 再繼續。
        /// </summary>
        private bool ProbeLoadQuery(ref MessageBasedSession s, string address, string query)
        {
            try
            {
                TryWrite(s, "*CLS");
                if (ParseNumber(Query(s, query)).HasValue && !HasScpiError(s)) return true;
            }
            catch
            {
                ReopenSession(ref s, address, ProbeTimeoutMs);
            }
            return false;
        }

        /// <summary>組出「設定拉載電流」的命令；未連線或探測失敗時退回 63xxx 的寫法。</summary>
        private string SetCurrentCommand(double amps)
        {
            var cs = _loadProfile != null ? _loadProfile.Commands : LoadCommandSets[0];
            return cs.SetCurrent + " " + amps.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private string LoadOffCommand()
        {
            var cs = _loadProfile != null ? _loadProfile.Commands : LoadCommandSets[0];
            return cs.OutputOff;
        }

        /// <summary>
        /// 儀器收尾：停背景迴圈 → 負載歸零關閉 → 兩台儀器切回本機模式 → 釋放 session。
        ///
        /// 先取號誌再送命令，避免背景 I/O 還在進行時就把 session 拆掉 ——
        /// 那會讓收尾命令（尤其是 GOTO_LOCAL）在例外中被跳過，機器就留在遠端狀態。
        /// 等不到號誌也照送：寧可讓 VISA 自己報錯，也不能不送。
        /// </summary>
        /// <returns>兩台儀器分別是否成功切回本機（沒連線的那台為 null）。</returns>
        private Tuple<bool?, bool?> ShutdownInstruments()
        {
            CancelAll();

            bool? loadLocal = null, meterLocal = null;

            if (_load != null)
            {
                bool acquired = _loadIo.Wait(3000);
                try
                {
                    TryWrite(_load, SetCurrentCommand(0));
                    TryWrite(_load, LoadOffCommand());
                    // DC Load 目前全為 Chroma 家族（63xxx / 62000D）。
                    loadLocal = ReturnToLocal(_load, MeterVendor.Chroma);
                }
                catch { }
                finally { if (acquired) _loadIo.Release(); }
            }

            if (_meter != null)
            {
                bool acquired = _meterIo.Wait(3000);
                try { meterLocal = ReturnToLocal(_meter, _profile != null ? _profile.Vendor : MeterVendor.Unknown); }
                catch { }
                finally { if (acquired) _meterIo.Release(); }
            }

            DisposeSession(ref _meter);
            DisposeSession(ref _load);
            return Tuple.Create(meterLocal, loadLocal);
        }

        private void DisconnectAll()
        {
            var local = ShutdownInstruments();

            // GOTO_LOCAL 沒送成功時明講，讓使用者知道機器可能還鎖在遠端模式，
            // 而不是面對一台按不動的儀器卻查不出原因。
            if (local.Item1 == false || local.Item2 == false)
                Log("※ 無法送出 VISA GOTO_LOCAL，儀器可能仍停在遠端控制模式；"
                  + "請改按機器前面板的 Local 鍵，或重新插拔 USB。");

            _profile = null;
            _loadProfile = null;
            _loadModel = "";
            _loadOutputOn = false;
            ClearReadouts();
            SetStatus("已中斷所有儀器連線。");
            UpdateUiState();
        }

        private async void btnMonitorStart_Click(object sender, EventArgs e)
        {
            if (_meter == null) { Warn("請先連線功率計。"); return; }
            if (_monitorCts != null) return;

            _monitorCts = new CancellationTokenSource();
            _loadReadErrorLogged = false;
            UpdateUiState();
            SetStatus("即時監看中…");

            var ct = _monitorCts.Token;
            int failures = 0;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var vals = await MeterExecAsync(() => ReadAll(), ct);
                        ShowValues(vals);

                        // 有連 DC Load 就順便讀負載端，拉載中即可同時看到輸出 V / I / W 與效率。
                        var load = await ReadLoadAsync(ct);
                        if (load != null) ShowLoadValues(load, CalcEfficiency(vals[Quantity.Watt], load.Watt));

                        failures = 0;
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        failures++;
                        Log($"讀取失敗（第 {failures} 次）：{ex.Message}");
                        if (failures >= MaxConsecutiveReadFailures)
                        {
                            Log("連續讀取失敗，已自動停止監看。請檢查連線後重新連線。");
                            SetStatus("連續讀取失敗，監看已停止。");
                            break;
                        }
                    }

                    try { await Task.Delay((int)numInterval.Value, ct); }
                    catch (OperationCanceledException) { break; }
                }
            }
            finally
            {
                _monitorCts?.Dispose();
                _monitorCts = null;
                UpdateUiState();
                if (UiAlive && lblStatus.Text == "即時監看中…") SetStatus("監看已停止。");
            }
        }

        private void btnMonitorStop_Click(object sender, EventArgs e)
        {
            try { _monitorCts?.Cancel(); } catch { }
        }

        private async void btnIntegStart_Click(object sender, EventArgs e)
        {
            if (_meter == null) { Warn("請先連線功率計。"); return; }
            if (_profile == null || !_profile.SupportsEnergy)
            {
                Warn("這台機器不支援 Wh（積分電能）量測，無法執行積分測試。");
                return;
            }
            if (_integCts != null) return;

            int integSec = (int)numIntegTime.Value;

            // 有連 DC Load 就跑「含積分的負載掃描」：逐點設定電流，每點穩定後才積分。
            // 沒連負載時退回單點積分，維持只有功率計時也能量測。
            bool sweepMode = _load != null;
            if (sweepMode && !ValidateIntegrationSweep(integSec)) return;

            _integCts = new CancellationTokenSource();
            UpdateUiState();
            try
            {
                if (sweepMode)
                {
                    await RunSweepAsync((double)numMaxCurrent.Value, (int)numStep.Value,
                                        (int)numDelay.Value, integSec, _integCts.Token);
                    return;
                }

                var res = await RunIntegrationCycleAsync(integSec, _integCts.Token);
                if (res != null)
                {
                    // 這條路徑沒有連 DC Load（有連就走上面的掃描），因此沒有負載端讀值與效率。
                    AddResultRow($"INTEG {integSec}s", null, res.Values, null, null, null, res.ThdSamples);
                    double? wh = res.Values[Quantity.WattHour];
                    if (wh.HasValue)
                        Log("積分完成，Wh = " + wh.Value.ToString("F5", CultureInfo.InvariantCulture));
                }
            }
            catch (OperationCanceledException)
            {
                SetIntegState("CANCELLED");
            }
            catch (Exception ex)
            {
                SetIntegState("ERROR");
                Log("積分流程失敗：" + ex.Message);
                SetStatus("積分流程失敗。");
            }
            finally
            {
                _integCts?.Dispose();
                _integCts = null;
                UpdateUiState();
            }
        }

        /// <summary>
        /// 積分掃描開跑前的參數檢查。Delay 與 Count 是相加的兩段時間，
        /// 不必互相容納，因此這裡只需確認負載電流有填。
        /// </summary>
        private bool ValidateIntegrationSweep(int integSec)
        {
            if ((double)numMaxCurrent.Value <= 0)
            {
                Warn("請先填入 DC Load 的最大電流（大於 0）。");
                return false;
            }
            return true;
        }

        private void btnIntegStop_Click(object sender, EventArgs e)
        {
            try { _integCts?.Cancel(); } catch { }
        }

        private async void btnSweepStart_Click(object sender, EventArgs e)
        {
            if (_load == null) { Warn("請先連線 DC Load。"); return; }
            if (_meter == null) { Warn("請先連線功率計，掃描過程需要讀取量測值。"); return; }
            if (_sweepCts != null) return;

            double maxCurrent = (double)numMaxCurrent.Value;
            if (maxCurrent <= 0) { Warn("請先填入 DC Load 的最大電流（大於 0）。"); return; }

            int delaySec = (int)numDelay.Value;

            _sweepCts = new CancellationTokenSource();
            UpdateUiState();
            try
            {
                await RunSweepAsync(maxCurrent, (int)numStep.Value, delaySec, 0, _sweepCts.Token);
            }
            catch (OperationCanceledException)
            {
                SetStatus("掃描已中止，負載已關閉。");
                Log("掃描已由使用者中止。");
            }
            catch (Exception ex)
            {
                Warn("掃描失敗：" + ex.Message);
            }
            finally
            {
                _sweepCts?.Dispose();
                _sweepCts = null;
                UpdateUiState();
            }
        }

        private void btnSweepStop_Click(object sender, EventArgs e)
        {
            try { _sweepCts?.Cancel(); } catch { }
        }

        /// <summary>
        /// 讀值來源切換。背景 I/O 不直接碰控制項，改讀這個欄位；
        /// 掃描進行中改也會即時生效，下一次讀值就換成新的來源。
        /// </summary>
        private void chkLoadMeasure_CheckedChanged(object sender, EventArgs e)
        {
            _loadUseMeasure = chkLoadMeasure.Checked;
        }

        /// <summary>
        /// 手動拉載開關。按下 Load ON 會先把畫面上的電流下給機器再開輸出；
        /// 再按一次則歸零並關閉輸出，順序與掃描流程一致 ——
        /// 先歸零再關，機器不會在下一次開啟時直接跳到上一筆的電流。
        ///
        /// 掃描／積分進行中會自行控制負載，那段期間這顆按鈕由 UpdateUiState 停用。
        /// </summary>
        private async void btnLoadOutput_Click(object sender, EventArgs e)
        {
            if (_load == null) { Warn("請先連線 DC Load。"); return; }

            var cs = _loadProfile != null ? _loadProfile.Commands : LoadCommandSets[0];
            bool turnOn = !_loadOutputOn;
            double amps = (double)numLoadSetI.Value;

            btnLoadOutput.Enabled = false;
            try
            {
                if (turnOn)
                {
                    string setCmd = SetCurrentCommand(amps);
                    await LoadRunAsync(() =>
                    {
                        TryWrite(_load, "*CLS");
                        // 62000D 這類雙向電源開機時可能停在 Source 模式，
                        // 不先切到 Load 模式，後面的電流設定會被機器忽略。
                        if (cs.EnterLoadMode != null) Write(_load, cs.EnterLoadMode);
                        Write(_load, setCmd);
                        // 電流超出機器的 LIMit 設定時命令會被拒絕，外觀上卻毫無異狀。
                        CheckLoadError(setCmd);
                        Write(_load, cs.OutputOn);
                        CheckLoadError("開啟負載（" + cs.OutputOn + "）");
                    }, CancellationToken.None);

                    _loadOutputOn = true;
                    SetStatus($"DC Load 已開啟，拉載 {amps:F2} A。");
                    Log($"手動拉載：{setCmd} → {cs.OutputOn}（{cs.Name}）。");
                }
                else
                {
                    await LoadRunAsync(() =>
                    {
                        Write(_load, SetCurrentCommand(0));
                        Write(_load, cs.OutputOff);
                    }, CancellationToken.None);

                    _loadOutputOn = false;
                    SetStatus("DC Load 已歸零並關閉。");
                    Log($"手動拉載結束：負載已歸零並關閉（{cs.OutputOff}）。");
                }
            }
            catch (Exception ex)
            {
                // 命令送到一半失敗時機器可能已經開始拉載，狀態一律當成未知後重新標為 OFF，
                // 使用者再按一次就會補送一次 OFF。
                _loadOutputOn = false;
                Warn((turnOn ? "開啟" : "關閉") + "負載失敗：" + ex.Message);
            }
            finally
            {
                UpdateUiState();
            }
        }

        /// <summary>
        /// 拉載中改電流就直接下給機器，不必先關再開。
        /// NumericUpDown 連續轉動會連發事件，這裡等一小段時間再取「當下」的值，
        /// 中間的過渡值就不會一筆一筆送到機器上。
        /// </summary>
        private async void numLoadSetI_ValueChanged(object sender, EventArgs e)
        {
            if (!_loadOutputOn || _load == null || _loadSetBusy) return;

            _loadSetBusy = true;
            try
            {
                await Task.Delay(200);
                double amps = (double)numLoadSetI.Value;
                string cmd = SetCurrentCommand(amps);
                await LoadRunAsync(() =>
                {
                    Write(_load, cmd);
                    CheckLoadError(cmd);
                }, CancellationToken.None);
                SetStatus($"DC Load 拉載電流已改為 {amps:F2} A。");
            }
            catch (Exception ex)
            {
                Log("設定拉載電流失敗：" + ex.Message);
            }
            finally
            {
                _loadSetBusy = false;
            }
        }

        /// <summary>
        /// 手動讀一次負載端數值。拉載中（LOAD ON）隨時可按，
        /// 不必等掃描跑到下一個點就能核對目前的輸出 V / I / W 與效率。
        /// </summary>
        private async void btnLoadRead_Click(object sender, EventArgs e)
        {
            if (_load == null) { Warn("請先連線 DC Load。"); return; }
            if (_loadProfile == null || !_loadProfile.ReadbackOk)
            {
                Warn("這台 DC Load 不支援 FETCh / MEASure 讀值。");
                return;
            }

            btnLoadRead.Enabled = false;
            try
            {
                _loadReadErrorLogged = false;

                // 功率計也一起讀，兩邊同時取樣才算得出對得起同一個工作點的效率。
                Dictionary<Quantity, double?> vals = null;
                if (_meter != null) vals = await MeterExecAsync(() => ReadAll(), CancellationToken.None);
                if (vals != null) ShowValues(vals);

                var load = await ReadLoadAsync(CancellationToken.None);
                if (load == null) { Warn("負載端讀值失敗。"); return; }

                double? meterWatt = vals != null ? vals[Quantity.Watt] : null;
                double? eff = CalcEfficiency(meterWatt, load.Watt);
                ShowLoadValues(load, eff);

                string note = FormatEfficiencyNote(meterWatt, load, eff);
                Log("負載端讀值完成" + (note.Length > 0 ? note : "")
                  + (_loadUseMeasure ? "（MEASure）" : "（FETCh）") + "。");
            }
            catch (Exception ex)
            {
                Warn("負載端讀值失敗：" + ex.Message);
            }
            finally
            {
                UpdateUiState();
            }
        }

        private void btnClearResults_Click(object sender, EventArgs e)
        {
            dgvResults.Rows.Clear();
        }

        private void btnExportCsv_Click(object sender, EventArgs e)
        {
            if (dgvResults.Rows.Count == 0) { Warn("目前沒有可匯出的資料。"); return; }

            using (var dlg = new SaveFileDialog())
            {
                dlg.Filter = "CSV 檔案 (*.csv)|*.csv";
                dlg.FileName = "meter_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine(string.Join(",", dgvResults.Columns.Cast<DataGridViewColumn>()
                                                    .Select(c => CsvCell(c.HeaderText))));
                    foreach (DataGridViewRow row in dgvResults.Rows)
                    {
                        if (row.IsNewRow) continue;
                        sb.AppendLine(string.Join(",", row.Cells.Cast<DataGridViewCell>()
                                                        .Select(c => CsvCell(Convert.ToString(c.Value)))));
                    }
                    // UTF-8 with BOM，讓 Excel 直接開啟時中文不會亂碼
                    File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
                    SetStatus("已匯出：" + dlg.FileName);
                    Log("已匯出 CSV：" + dlg.FileName);
                }
                catch (Exception ex)
                {
                    Warn("匯出失敗：" + ex.Message);
                }
            }
        }

        private static string CsvCell(string text)
        {
            text = text ?? "";
            return text.Contains(",") || text.Contains("\"") ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
        }

        #endregion

        #region UI 更新

        /// <summary>
        /// 表單是否還能安全操作 UI。背景迴圈的 await 有可能在表單關閉之後才恢復執行，
        /// 這時候碰控制項會丟 ObjectDisposedException，因此所有 UI 更新前都先檢查這裡。
        /// </summary>
        private bool UiAlive => !IsDisposed && !Disposing && IsHandleCreated;

        private static string Abbrev(Quantity q)
        {
            switch (q)
            {
                case Quantity.Voltage:     return "V";
                case Quantity.Current:     return "I";
                case Quantity.Watt:        return "W";
                case Quantity.PowerFactor: return "PF";
                case Quantity.Vthd:        return "VTHD";
                case Quantity.Ithd:        return "ITHD";
                case Quantity.WattHour:    return "WH";
                default:                   return q.ToString();
            }
        }

        private static string FormatOf(Quantity q)
        {
            switch (q)
            {
                case Quantity.Voltage:     return "F3";
                case Quantity.Current:     return "F4";
                case Quantity.Watt:        return "F3";
                case Quantity.PowerFactor: return "F4";
                case Quantity.WattHour:    return "F5";
                default:                   return "F2";   // THD
            }
        }

        private void InitResultGrid()
        {
            dgvResults.Columns.Clear();
            dgvResults.Columns.Add("colTime", "Time");
            dgvResults.Columns.Add("colPoint", "Point");
            dgvResults.Columns.Add("colIset", "Iset (A)");
            foreach (var q in AllQuantities)
                dgvResults.Columns.Add("col" + q, Abbrev(q));
            // 負載端（待測物輸出側）讀值與效率，緊接在功率計欄位之後，一列就能看出輸入 vs 輸出。
            dgvResults.Columns.Add("colLoadV", "L-V");
            dgvResults.Columns.Add("colLoadI", "L-I");
            dgvResults.Columns.Add("colLoadW", "L-W");
            dgvResults.Columns.Add("colEff", "Eff%");
            dgvResults.Columns.Add("colDeltaWh", "dWh");
            dgvResults.Columns.Add("colThdN", "THDn");

            foreach (DataGridViewColumn c in dgvResults.Columns)
            {
                c.SortMode = DataGridViewColumnSortMode.NotSortable;
                c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
                c.MinimumWidth = 36;
            }

            // 欄位多、表格窄，平均分配會讓時間欄被截成 "12:34:5…"。
            // 依實際內容長度配權重：時間最寬，Point / Iset / THDn 這些短內容讓出空間給數值欄。
            dgvResults.Columns["colTime"].FillWeight = 115;
            dgvResults.Columns["colPoint"].FillWeight = 75;
            dgvResults.Columns["colIset"].FillWeight = 85;
            dgvResults.Columns["colEff"].FillWeight = 90;
            dgvResults.Columns["colThdN"].FillWeight = 65;
            dgvResults.Columns["colLoadV"].ToolTipText = "DC Load 讀回的輸出電壓（待測物輸出側）。";
            dgvResults.Columns["colLoadI"].ToolTipText =
                "DC Load 讀回的輸出電流。機器拉載時回傳負值（表示汲取），這裡一律換算成正值。";
            dgvResults.Columns["colLoadW"].ToolTipText =
                "DC Load 讀回的輸出功率。機器不支援功率查詢時改由 L-V × L-I 推算。";
            dgvResults.Columns["colEff"].ToolTipText =
                "效率 = L-W ÷ W × 100%（負載端輸出功率 ÷ 功率計輸入功率）。";
            dgvResults.Columns["colDeltaWh"].ToolTipText =
                "與上一個負載點的 Wh 差值。若儀器的積分值是跨負載點累加而非每點歸零，這一欄才是該點實際消耗的能量。";
            dgvResults.Columns["colThdN"].ToolTipText =
                "VTHD / ITHD 的取樣筆數。> 0 表示該列的 THD 是積分期間的平均值；0 表示是單筆瞬時讀值。";
        }

        private void AddResultRow(string point, double? iset, Dictionary<Quantity, double?> vals,
                                  LoadReading load, double? efficiency, double? deltaWh, int thdSamples)
        {
            if (!UiAlive) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => AddResultRow(point, iset, vals, load, efficiency,
                                                          deltaWh, thdSamples)));
                return;
            }

            var cells = new List<object>
            {
                DateTime.Now.ToString("HH:mm:ss"),
                point,
                iset.HasValue ? iset.Value.ToString("F2", CultureInfo.InvariantCulture) : ""
            };
            foreach (var q in AllQuantities)
            {
                double? v;
                cells.Add(vals.TryGetValue(q, out v) && v.HasValue
                    ? v.Value.ToString(FormatOf(q), CultureInfo.InvariantCulture)
                    : "N/A");
            }
            cells.Add(Cell(load?.Voltage, "F3"));
            cells.Add(Cell(load?.Current, "F4"));
            cells.Add(Cell(load?.Watt, "F3"));
            cells.Add(Cell(efficiency, "F2"));
            cells.Add(deltaWh.HasValue ? deltaWh.Value.ToString("F5", CultureInfo.InvariantCulture) : "");
            cells.Add(thdSamples > 0 ? thdSamples.ToString(CultureInfo.InvariantCulture) : "");

            int idx = dgvResults.Rows.Add(cells.ToArray());

            // 效率大於 100% 物理上不可能，多半是量測端接反或接錯，用紅字標出來而不是默默記下去。
            if (efficiency.HasValue && efficiency.Value > SuspiciousEfficiencyPct)
                dgvResults.Rows[idx].Cells["colEff"].Style.ForeColor = Color.Firebrick;

            dgvResults.FirstDisplayedScrollingRowIndex = idx;
        }

        /// <summary>Result 表的數值格子：沒有值就寫 N/A，維持與功率計欄位一致的呈現方式。</summary>
        private static string Cell(double? v, string format)
        {
            return v.HasValue ? v.Value.ToString(format, CultureInfo.InvariantCulture) : "N/A";
        }

        private static string FormatDuration(int seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours} 小時 {t.Minutes} 分"
                : $"{t.Minutes} 分 {t.Seconds} 秒";
        }

        private void SweepSetting_Changed(object sender, EventArgs e)
        {
            UpdateSweepEstimate();
            UpdateIntegNote();
        }

        /// <summary>把目前設定換算成「幾點 / 總時長」顯示在旁邊，設定錯了按下去之前就看得出來。</summary>
        private void UpdateSweepEstimate()
        {
            if (!UiAlive) return;

            int points = BuildSweepPoints((int)numStep.Value).Count;
            int delaySec = (int)numDelay.Value;

            // 這裡只估 Start Sweep（純掃描）；積分掃描還要加上 Count，總時間顯示在 Integration 區。
            lblSweepEstimate.Text = $"{points} 點 × {delaySec}s，約 {FormatDuration(points * delaySec)}";
            lblSweepEstimate.ForeColor = Color.DimGray;
        }

        private void ShowValues(Dictionary<Quantity, double?> vals)
        {
            if (!UiAlive) return;
            if (InvokeRequired) { BeginInvoke(new Action(() => ShowValues(vals))); return; }

            foreach (var q in AllQuantities)
            {
                var box = _readouts[q];
                double? v;
                bool has = vals.TryGetValue(q, out v) && v.HasValue;
                box.Text = has ? v.Value.ToString(FormatOf(q), CultureInfo.InvariantCulture) : "N/A";
                box.ForeColor = has ? SystemColors.WindowText : Color.DimGray;
            }
        }

        /// <summary>清空讀值，並把「這台機器不支援」的欄位標成灰底，讓不支援與讀不到一眼可分。</summary>
        private void ClearReadouts()
        {
            if (!UiAlive) return;
            foreach (var q in AllQuantities)
            {
                var box = _readouts[q];
                bool supported = _profile != null && _profile.Supported.Contains(q);
                box.Text = _profile == null ? "" : (supported ? "" : "N/A");
                box.ForeColor = SystemColors.WindowText;
                box.BackColor = (_profile != null && !supported) ? Color.FromArgb(240, 240, 240) : SystemColors.Window;
            }
            ClearLoadReadouts();
        }

        /// <summary>更新負載端讀值與效率的顯示。</summary>
        private void ShowLoadValues(LoadReading r, double? efficiency)
        {
            if (!UiAlive) return;
            if (InvokeRequired) { BeginInvoke(new Action(() => ShowLoadValues(r, efficiency))); return; }

            SetReadout(txtLoadV, r?.Voltage, "F3");
            SetReadout(txtLoadI, r?.Current, "F4");
            SetReadout(txtLoadW, r?.Watt, "F3");
            SetReadout(txtEff, efficiency, "F2");

            // 效率有值時用綠字凸顯；超過 100% 一定是接線或量測端有問題，改成紅字。
            if (efficiency.HasValue)
                txtEff.ForeColor = efficiency.Value > SuspiciousEfficiencyPct ? Color.Firebrick : Color.DarkGreen;
        }

        private static void SetReadout(TextBox box, double? v, string format)
        {
            box.Text = v.HasValue ? v.Value.ToString(format, CultureInfo.InvariantCulture) : "N/A";
            box.ForeColor = v.HasValue ? SystemColors.WindowText : Color.DimGray;
        }

        /// <summary>
        /// 清空負載端讀值，並把「這台負載讀不到」的欄位標成灰底 ——
        /// 與功率計欄位一樣，讓「不支援」和「還沒讀」一眼可分。
        /// </summary>
        private void ClearLoadReadouts()
        {
            if (!UiAlive) return;

            var lp = _loadProfile;
            var boxes = new[] { txtLoadV, txtLoadI, txtLoadW, txtEff };
            var supported = new[]
            {
                lp != null && lp.CanReadVoltage,
                lp != null && lp.CanReadCurrent,
                lp != null && (lp.CanReadPower || lp.CanComputePower),
                lp != null && (lp.CanReadPower || lp.CanComputePower),   // 效率靠負載端功率
            };

            for (int i = 0; i < boxes.Length; i++)
            {
                boxes[i].Text = lp == null ? "" : (supported[i] ? "" : "N/A");
                boxes[i].ForeColor = SystemColors.WindowText;
                boxes[i].BackColor = (lp != null && !supported[i])
                    ? Color.FromArgb(240, 240, 240) : SystemColors.Window;
            }
            UpdateLoadNote();
        }

        /// <summary>負載讀值區的說明文字：讀值來源、效率定義，以及探測不到時的提醒。</summary>
        private void UpdateLoadNote()
        {
            if (!UiAlive) return;

            var lp = _loadProfile;
            if (lp == null)
            {
                lblLoadNote.ForeColor = Color.DimGray;
                lblLoadNote.Text = "※ 未連線 DC Load。連線後拉載期間會即時讀回輸出 V / I / W 並計算效率。";
                return;
            }
            if (!lp.ReadbackOk)
            {
                lblLoadNote.ForeColor = Color.Firebrick;
                lblLoadNote.Text = $"※ 探測結果：{lp.Model} 不支援 FETCh 讀值，負載端數值與效率無法取得。";
                return;
            }

            lblLoadNote.ForeColor = Color.DimGray;
            lblLoadNote.Text = "※ 效率 = 負載端 W ÷ 功率計 W × 100%（輸出 ÷ 輸入）。"
                             + "拉載時機器回傳的 I / W 帶負號表示汲取，畫面一律換算成正值。"
                             + (lp.CanComputePower ? "　本機不支援功率查詢，W 由 V × I 推算。" : "");
        }

        /// <summary>把這一點的輸入 / 輸出功率與效率接在 Log 後面，掃描時不必盯著表格也看得到。</summary>
        private static string FormatEfficiencyNote(double? meterWatt, LoadReading load, double? efficiency)
        {
            if (load == null || !load.Any) return "";
            var parts = new List<string>();
            if (meterWatt.HasValue) parts.Add($"輸入 {Math.Abs(meterWatt.Value):F3} W");
            if (load.Watt.HasValue) parts.Add($"輸出 {load.Watt.Value:F3} W");
            if (efficiency.HasValue) parts.Add($"效率 {efficiency.Value:F2}%");
            return parts.Count == 0 ? "" : "（" + string.Join("、", parts) + "）";
        }

        private void SetIntegState(string state)
        {
            if (!UiAlive) return;
            if (InvokeRequired) { BeginInvoke(new Action(() => SetIntegState(state))); return; }
            lblIntegState.Text = "State: " + (string.IsNullOrEmpty(state) ? "--" : state);
        }

        private void SetIntegResult(double? wh)
        {
            if (!UiAlive) return;
            if (InvokeRequired) { BeginInvoke(new Action(() => SetIntegResult(wh))); return; }
            lblIntegResult.Text = wh.HasValue
                ? "Wh: " + wh.Value.ToString("F5", CultureInfo.InvariantCulture)
                : "Wh: --";
            lblIntegResult.ForeColor = wh.HasValue ? Color.DarkGreen : SystemColors.ControlText;
        }

        private void UpdateIntegProgress(string state, double elapsed, int total)
        {
            if (!UiAlive) return;
            if (InvokeRequired) { BeginInvoke(new Action(() => UpdateIntegProgress(state, elapsed, total))); return; }
            lblIntegState.Text = $"State: {state}    {elapsed:F0} / {total} sec";
            int pct = total <= 0 ? 0 : (int)Math.Min(100, Math.Max(0, elapsed / total * 100));
            pbInteg.Value = pct;
        }

        /// <summary>依連線與忙碌狀態統一決定每個控制項能不能按，避免各處各自為政。</summary>
        private void UpdateUiState()
        {
            if (!UiAlive) return;

            bool meterOn = _meter != null;
            bool loadOn = _load != null;
            bool monitoring = _monitorCts != null;
            bool integrating = _integCts != null;
            bool sweeping = _sweepCts != null;
            bool busy = monitoring || integrating || sweeping;
            bool energyOk = _profile != null && _profile.SupportsEnergy;

            btnConnectAll.Text = (meterOn || loadOn) ? "Disconnect" : "Connect";
            btnConnectAll.Enabled = !busy;
            btnScanVisa.Enabled = !busy && !meterOn && !loadOn;
            cbbMeter.Enabled = !meterOn && !loadOn;
            cbbLoad.Enabled = !meterOn && !loadOn;
            UpdateVisaSummary();
            UpdateMeterInfoLabel();

            btnMonitorStart.Enabled = meterOn && !monitoring && !integrating && !sweeping;
            btnMonitorStop.Enabled = monitoring;
            numInterval.Enabled = !monitoring;

            // 不支援 Wh 的機器（例如 Chroma 66202）整組積分功能停用，
            // 避免送出 FETCh? WH 之類機器不認識的命令造成逾時。
            gbIntegration.Enabled = meterOn;
            btnIntegStart.Enabled = meterOn && energyOk && !integrating && !sweeping;
            btnIntegStop.Enabled = integrating;
            numIntegTime.Enabled = energyOk && !integrating && !sweeping;

            // 負載端讀值：連上負載就顯示，但手動讀取只在沒有其他流程佔用 VISA 時開放。
            gbLoadRead.Enabled = loadOn;
            btnLoadRead.Enabled = loadOn && !busy && _loadProfile != null && _loadProfile.ReadbackOk;

            // 手動拉載：掃描與積分會自己控制負載，那段期間不開放手動介入；
            // 單純監看功率計只用到功率計的 session，不影響這裡。
            bool manualLoadOk = loadOn && !sweeping && !integrating;
            numLoadSetI.Enabled = manualLoadOk;
            btnLoadOutput.Enabled = manualLoadOk;
            btnLoadOutput.Text = _loadOutputOn ? "Load OFF" : "Load ON";
            btnLoadOutput.ForeColor = _loadOutputOn ? Color.Firebrick : SystemColors.ControlText;

            UpdateLoadNote();

            btnSweepStart.Enabled = loadOn && meterOn && !sweeping && !integrating;
            btnSweepStop.Enabled = sweeping;
            // 積分掃描同樣吃這三個參數，兩種掃描進行中都不能改。
            numMaxCurrent.Enabled = !sweeping && !integrating;
            numStep.Enabled = !sweeping && !integrating;
            numDelay.Enabled = !sweeping && !integrating;
            UpdateSweepEstimate();
            UpdateIntegNote();
        }

        /// <summary>
        /// Integration 區的說明文字與總時間預估。Start Integration 的行為會隨 DC Load 是否連線
        /// 而不同，時間切分與總時長都要在按下去之前就看得到，不能等跑完才發現設定不是想要的。
        /// 預估標籤與 DC Load Sweep 那邊同一個寫法（幾點 × 每點秒數，約多久）。
        /// </summary>
        private void UpdateIntegNote()
        {
            if (!UiAlive) return;

            lblIntegNote.ForeColor = Color.DimGray;

            int integSec = (int)numIntegTime.Value;
            int delaySec = (int)numDelay.Value;
            int pointSec = delaySec + integSec;
            int holdSec = Math.Min(SweepPointBufferSeconds, delaySec);
            int points = BuildSweepPoints((int)numStep.Value).Count;

            // 總時間預估與 DC Load Sweep 那邊同一個寫法，連線之前就先算給人看；
            // 沒連負載走的是單點積分，總時間就是 Count 本身。
            lblIntegEstimate.Text = _load != null
                ? $"{points} 點 × {pointSec}s，約 {FormatDuration(points * pointSec)}"
                : $"單點 × {integSec}s，約 {FormatDuration(integSec)}";

            if (_meter == null) { lblIntegNote.Text = ""; return; }
            if (_profile == null || !_profile.SupportsEnergy)
            {
                lblIntegNote.ForeColor = Color.Firebrick;
                lblIntegNote.Text = $"※ 探測結果：{_profile?.Model} 不支援 WH，積分功能停用。";
                lblIntegEstimate.Text = "";
                return;
            }
            if (_load == null)
            {
                lblIntegNote.Text = "※ 未連線 DC Load，Start Integration 只做一次單點積分。";
                return;
            }

            lblIntegNote.Text = $"※ 每點 {pointSec}s ＝ 穩定 {delaySec}s（Sweep 的 Delay）＋ 積分 {integSec}s；"
                              + $"上一點的讀值會留到穩定期第 {holdSec}s，之後才換成新值。";
        }

        /// <summary>群組標題直接當成收合後的狀態摘要，不必再多開一個控制項。</summary>
        private void UpdateVisaSummary()
        {
            var parts = new List<string>();
            if (_profile != null) parts.Add("Meter " + _profile.Model);
            if (_load != null) parts.Add("Load " + _loadModel);

            gbInstruments.Text = parts.Count == 0
                ? "Instruments (VISA) — 未連線"
                : "Instruments (VISA) — 已連線：" + string.Join(" + ", parts);
        }

        private void UpdateMeterInfoLabel()
        {
            if (_profile != null)
            {
                lblMeterInfo.Text = $"{_profile.Idn}    可用參數：{string.Join(", ", _profile.Supported.Select(Abbrev))}"
                                  + (_profile.CompositeOk ? "    [複合查詢]" : "    [逐項查詢]");
                lblMeterInfo.ForeColor = Color.DarkGreen;
            }
            else if (_load != null)
            {
                lblMeterInfo.Text = "只連上 DC Load，未連線功率計。";
                lblMeterInfo.ForeColor = Color.DarkOrange;
            }
            else
            {
                lblMeterInfo.Text = "Not connected.";
                lblMeterInfo.ForeColor = Color.DimGray;
            }
        }

        private void SetStatus(string text)
        {
            if (!UiAlive) return;
            if (InvokeRequired) { BeginInvoke(new Action(() => SetStatus(text))); return; }
            lblStatus.Text = text;
            lblStatus.ForeColor = SystemColors.ControlText;
        }

        /// <summary>
        /// 訊息只在視窗最底下的狀態列顯示最新一筆；前幾筆保留在 ToolTip 裡（滑鼠移到狀態列可看），
        /// 完整內容另外送到 Debug 輸出，方便接除錯器時追蹤。
        /// </summary>
        private void Log(string text)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] {text}");
            if (!UiAlive) return;
            if (InvokeRequired) { BeginInvoke(new Action(() => Log(text))); return; }

            string line = $"[{DateTime.Now:HH:mm:ss}] {text}";
            _logHistory.Enqueue(line);
            while (_logHistory.Count > LogHistoryLines) _logHistory.Dequeue();

            lblStatus.Text = line;
            lblStatus.ToolTipText = string.Join(Environment.NewLine, _logHistory);
            lblStatus.ForeColor = SystemColors.ControlText;
        }

        /// <summary>
        /// 需要使用者注意的訊息（未連線、參數不合理、操作失敗）。
        /// 刻意不彈窗 —— 測試流程常常一路按到底，跳窗會擋住操作；
        /// 改成狀態列紅字，內容一樣進入 ToolTip 的訊息歷程，滑鼠移上去看得到前幾筆。
        /// </summary>
        private void Warn(string text)
        {
            Log(text);
            if (!UiAlive) return;
            // 只把上色動作丟回 UI 執行緒；整個 Warn 重跑會讓同一則訊息記錄兩次。
            Action mark = () => lblStatus.ForeColor = Color.Firebrick;
            if (InvokeRequired) BeginInvoke(mark); else mark();
        }

        #endregion
    }
}
