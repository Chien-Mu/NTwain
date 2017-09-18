using CommonWin32;
using NTwain;
using NTwain.Data;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace Tester.Winform
{
    sealed partial class TestForm : Form
    {
        ImageCodecInfo _tiffCodecInfo;
        TwainSession _twain;
        bool _stopScan;
        bool _loadingCaps;


        #region setup & cleanup

        public TestForm()
        {
            InitializeComponent();
            if (IntPtr.Size == 8)
            {
                Text = Text + " (64bit)";
            }
            else
            {
                Text = Text + " (32bit)";
            }
            foreach (var enc in ImageCodecInfo.GetImageEncoders())
            {
                if (enc.MimeType == "image/tiff") { _tiffCodecInfo = enc; break; }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_twain != null)
            {
                if (e.CloseReason == CloseReason.UserClosing && _twain.State > 4)
                {
                    e.Cancel = true;
                }
                else
                {
                    CleanupTwain();
                }
            }
            base.OnFormClosing(e);
        }

        private void SetupTwain()
        {
            //Source Manager 就是連接設備與PC的程式，而TWAIN 就是 Source Manager 的 interface #1-2
            //appId 就是紀錄 Source Manager 通訊狀態的 struct
            var appId = TWIdentity.CreateFromAssembly(DataGroups.Image, Assembly.GetEntryAssembly());

            _twain = new TwainSession(appId);
            // either set this and don't worry about threads during events,
            // or don't and invoke during the events yourself
            //_twain.SynchronizationContext = SynchronizationContext.Current;
            _twain.StateChanged += (s, e) =>
            {
                Debug.WriteLine("State changed to " + _twain.State + " on thread " + Thread.CurrentThread.ManagedThreadId);
            };

            //轉入圖像
            _twain.DataTransferred += (s, e) =>
            {
                Bitmap img = null;
                if (e.NativeData != IntPtr.Zero)
                {
                    img = e.NativeData.GetDrawingBitmap();
                }
                else if (!string.IsNullOrEmpty(e.FileDataPath))
                {
                    img = new Bitmap(e.FileDataPath);
                }
                if (img != null)
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        if (pictureBox1.Image != null)
                        {
                            pictureBox1.Image.Dispose();
                            pictureBox1.Image = null;
                        }
                        pictureBox1.Image = img;
                    }));
                }
            };
            _twain.SourceDisabled += (s, e) =>
            {
                this.BeginInvoke(new Action(() =>
                {
                    btnStopScan.Enabled = false;
                    btnStartCapture.Enabled = true;
                    panelOptions.Enabled = true;
                    LoadSourceCaps();
                }));
            };
            _twain.TransferReady += (s, e) =>
            {
                e.CancelAll = _stopScan;
            };
        }
        // 清除  #3-11 State 表
        private void CleanupTwain()
        {
            if (_twain.State == 4)
            {
                _twain.CurrentSource.Close();
            }
            if (_twain.State == 3)
            {
                _twain.Close();
            }

            if (_twain.State > 2)
            {
                // normal close down didn't work, do hard kill
                _twain.ForceStepDown(2);
            }
        }

        #endregion

        #region toolbar

        //他的用意是要按 btnSources 就讀 ReloadSourceList()
        private void btnSources_DropDownOpening(object sender, EventArgs e)
        {
            if (btnSources.DropDownItems.Count == 2) //這句好像沒意義，load初始就 count == 2了
            {
                ReloadSourceList();
            }
        }

        private void reloadSourcesListToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ReloadSourceList();
        }

        void SourceMenuItem_Click(object sender, EventArgs e)
        {
            // do nothing if source is enabled
            //已經到 state 5 啟動狀態，則許消此操作
            if (_twain.State > 4) { return; }

            //到 state 4 還有救，關閉後即可
            if (_twain.State == 4) { _twain.CurrentSource.Close(); }

            //每個項目都設定 checked == false
            foreach (var btn in btnSources.DropDownItems)
            {
                var srcBtn = btn as ToolStripMenuItem;
                if (srcBtn != null) { srcBtn.Checked = false; }
            }

            var curBtn = (sender as ToolStripMenuItem); //拿到使用者案的 srcBtn
            var src = curBtn.Tag as TwainSource;
            if (src.Open() == ReturnCode.Success) //open source manager 且若回傳 Success
            {
                curBtn.Checked = true;
                btnStartCapture.Enabled = true; //start scan 按鈕
                LoadSourceCaps();
            }
        }

        private void btnStartCapture_Click(object sender, EventArgs e)
        {
            if (_twain.State == 4)
            {
                _stopScan = false;
                //CapUIControllable 是否支援 UI disabled (這裡的UI 指的是 driver 附的較詳細的控制介面UI) #10-11
                if (_twain.CurrentSource.SupportedCaps.Contains(CapabilityId.CapUIControllable))
                {
                    // hide scanner ui if possible.  隱藏UI
                    if (_twain.CurrentSource.Enable(SourceEnableMode.NoUI, false, this.Handle) == ReturnCode.Success) //這行就是執行了
                    {
                        btnStopScan.Enabled = true;
                        btnStartCapture.Enabled = false;
                        panelOptions.Enabled = false;
                    }
                }
                else
                {
                    if (_twain.CurrentSource.Enable(SourceEnableMode.ShowUI, true, this.Handle) == ReturnCode.Success) //如果沒支援，只好 SHOW UI
                    {
                        btnStopScan.Enabled = true;
                        btnStartCapture.Enabled = false;
                        panelOptions.Enabled = false;
                    }
                }
            }
        }

        private void btnStopScan_Click(object sender, EventArgs e)
        {
            _stopScan = true;
        }

        private void btnSaveImage_Click(object sender, EventArgs e)
        {
            var img = pictureBox1.Image;

            if (img != null)
            {
                switch (img.PixelFormat)
                {
                    case PixelFormat.Format1bppIndexed:
                        saveFileDialog1.Filter = "tiff files|*.tif";
                        break;
                    default:
                        saveFileDialog1.Filter = "png files|*.png";
                        break;
                }

                if (saveFileDialog1.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    if (saveFileDialog1.FileName.EndsWith(".tif", StringComparison.OrdinalIgnoreCase))
                    {
                        EncoderParameters tiffParam = new EncoderParameters(1);

                        tiffParam.Param[0] = new EncoderParameter(Encoder.Compression, (long)EncoderValue.CompressionCCITT4);

                        pictureBox1.Image.Save(saveFileDialog1.FileName, _tiffCodecInfo, tiffParam);
                    }
                    else
                    {
                        pictureBox1.Image.Save(saveFileDialog1.FileName, ImageFormat.Png);
                    }
                }
            }
        }
        #endregion

        #region real work

        private void ReloadSourceList()
        {
            if (_twain == null)
            {
                SetupTwain();  //只能建立一次， 所以加在 _twain == null 下
            }
            if (_twain.State < 3)
            {
                // use this for internal msg loop
                _twain.Open();
                // use this to hook into current app loop
                //_twain.Open(new WindowsFormsMessageLoopHook(this.Handle));
            }

            if (_twain.State >= 3)
            {
                //sepSourceList 就是 btnSourcse combobox 裡面那條線橫線，那條線index大於0，代表有撈到 source manager 資料    
                //一開始btnSources.DropDownItems.IndexOf(sepSourceList) 就是 0，也就是 有 source manager 才執行
                while (btnSources.DropDownItems.IndexOf(sepSourceList) > 0)
                {
                    //把 btnSources itme 都移除
                    var first = btnSources.DropDownItems[0];
                    first.Click -= SourceMenuItem_Click;
                    btnSources.DropDownItems.Remove(first);
                }

                //開始安插 source manager
                foreach (var src in _twain.GetSources()) //只能call 到 state >= 2 的 source manager (method上寫的
                {
                    var srcBtn = new ToolStripMenuItem(src.Name);
                    srcBtn.Tag = src; //設定srcBtn 就是 source manager 的物件
                    srcBtn.Click += SourceMenuItem_Click;  //delegate (這裡只是給 Method address，argument:sender、e 是 srcBtn.Click 內部去給的
                    srcBtn.Checked = _twain.CurrentSource != null && _twain.CurrentSource.Name == src.Name;
                    btnSources.DropDownItems.Insert(0, srcBtn);
                }
            }
        }


        #region cap control


        private void LoadSourceCaps()
        {
            var caps = _twain.CurrentSource.SupportedCaps; //GET 出 Capability 
            // get 已 open 的 soruce manager,因為前面呼叫有 src.open()，CurrentSource 就是 正在使用的
            // Capabilities 說明 #2-14
            
            _loadingCaps = true;
            if (groupDepth.Enabled = caps.Contains(CapabilityId.ICapPixelType))
            {
                LoadDepth();
            }
            if (groupDPI.Enabled = caps.Contains(CapabilityId.ICapXResolution) && caps.Contains(CapabilityId.ICapYResolution))
            {
                LoadDPI();
            }
            // TODO: find out if this is how duplex works or also needs the other option
            if (groupDuplex.Enabled = caps.Contains(CapabilityId.CapDuplexEnabled))
            {
                LoadDuplex();
            }
            if (groupSize.Enabled = caps.Contains(CapabilityId.ICapSupportedSizes))
            {
                LoadPaperSize();
            }
            btnAllSettings.Enabled = caps.Contains(CapabilityId.CapEnableDSUIOnly);
            
            //ADF test #10-9 #10-61
            if(caps.Contains(CapabilityId.CapFeederEnabled) && caps.Contains(CapabilityId.CapFeederPrep))
            {
                Console.WriteLine("有支援ADF");
            }
            _loadingCaps = false;
        }

        private void LoadPaperSize()
        {
            var list = _twain.CurrentSource.CapGetSupportedSizes();
            comboSize.DataSource = list;
            var cur = _twain.CurrentSource.CapGetCurrent(CapabilityId.ICapSupportedSizes).ConvertToEnum<SupportedSize>();
            if (list.Contains(cur))
            {
                comboSize.SelectedItem = cur; //選取的項目 設定物件(這樣沒選的項目，不就沒有被設定到物件???)
            }
        }

        private void LoadDuplex()
        {
            ckDuplex.Checked = _twain.CurrentSource.CapGetCurrent(CapabilityId.CapDuplexEnabled).ConvertToEnum<uint>() != 0;
        }

        private void LoadDPI()
        {
            // only allow dpi of certain values for those source that lists everything
            var list = _twain.CurrentSource.CapGetDPIs().Where(dpi => (dpi % 50) == 0).ToList();
            comboDPI.DataSource = list;
            var cur = (TWFix32)_twain.CurrentSource.CapGetCurrent(CapabilityId.ICapXResolution);
            if (list.Contains(cur))
            {
                comboDPI.SelectedItem = cur;
            }
        }

        private void LoadDepth()
        {
            var list = _twain.CurrentSource.CapGetPixelTypes();
            comboDepth.DataSource = list;
            var cur = _twain.CurrentSource.CapGetCurrent(CapabilityId.ICapPixelType).ConvertToEnum<PixelType>();
            if (list.Contains(cur))
            {
                comboDepth.SelectedItem = cur;
            }
        }

        private void comboSize_SelectedIndexChanged(object sender, EventArgs e)
        {
            //將選擇的item 物件載入 CurrentSource
            if (!_loadingCaps && _twain.State == 4)
            {
                var sel = (SupportedSize)comboSize.SelectedItem;
                _twain.CurrentSource.CapSetSupportedSize(sel);
            }
        }

        private void comboDepth_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (!_loadingCaps && _twain.State == 4)
            {
                var sel = (PixelType)comboDepth.SelectedItem;
                _twain.CurrentSource.CapSetPixelType(sel);
            }
        }

        private void comboDPI_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (!_loadingCaps && _twain.State == 4)
            {
                var sel = (TWFix32)comboDPI.SelectedItem;
                _twain.CurrentSource.CapSetDPI(sel);
            }
        }

        private void ckDuplex_CheckedChanged(object sender, EventArgs e)
        {
            if (!_loadingCaps && _twain.State == 4)
            {
                _twain.CurrentSource.CapSetDuplex(ckDuplex.Checked);
            }
        }

        private void btnAllSettings_Click(object sender, EventArgs e)
        {
            _twain.CurrentSource.Enable(SourceEnableMode.ShowUIOnly, true, this.Handle);
        }

        #endregion

        private void TestForm_Load(object sender, EventArgs e)
        {
            Console.WriteLine(btnSources.DropDownItems.Count);
        }

        #endregion

    }
}
