﻿using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using F1Speed.Core;
using System.Runtime.InteropServices;
using F1Speed.Core.Repositories;
using log4net;
using log4net.Core;
using System.Collections.Generic;

namespace F1Speed
{
    public partial class Form1 : Form
    {
        private delegate void WriteLogCallback(string text);

        private delegate void UpdateCircuitCallback(string circuitName, string lapType);

        private static ILog logger = Logger.Create();

        // Constants
        private const int PORTNUM = 20777;
        private const string IP = "127.0.0.1";
        private const int TIMERINTERVAL_MS = 100;        // refresh display every 10th of a sec

        // This is the IP endpoint we are connecting to (i.e. the IP Address and Port F1 2012 is sending to)
        //private IPEndPoint remoteIP = new IPEndPoint(IPAddress.Parse(IP), PORTNUM);        
        private IPEndPoint remoteIP;
        // This is the IP endpoint for capturing who actually sent the data
        private IPEndPoint senderIP = new IPEndPoint(IPAddress.Any, 0);
        // UDP Socket for the connection
        private UdpClient udpSocket;

        // Thread for capturing telemtry
        private Thread telemCaptureThread = null;

        // Holds the latest data captured from the game
        TelemetryPacket latestData;

        // Mutex used to protect latestData from simultaneous access by both threads
        static Mutex syncMutex = new Mutex();

        private TelemetryLapManager manager;

        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const UInt32 SWP_NOSIZE = 0x0001;
        const UInt32 SWP_NOMOVE = 0x0002;
        const UInt32 TOPMOST_FLAGS = SWP_NOMOVE | SWP_NOSIZE;

        //[DllImport("user32.dll")]
        //[return: MarshalAs(UnmanagedType.Bool)]
        //public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        public Form1(TelemetryLapManager telemetryLapManager)
        {
            InitializeComponent();

            
            manager = telemetryLapManager;

            var type = typeof(TelemetryPacket);
            var fields = type.GetFields();
            cboField.Items.Clear();
            foreach (var field in fields)
            {
                cboField.Items.Add(field.Name);
            }

            cboField.SelectedIndex = 0; // default to first item

            manager.CircuitChanged += (s, e) => UpdateCircuitName(e.CircuitName, e.LapType);            

#if DEBUG
            this.Height = 980;

            manager.CompletedFullLap += (s, e) => writeLog(string.Format("Completed Full Lap.  Last={0}...Current={1}", e.PreviousLapNumber, e.CurrentLapNumber));
            manager.RemovedLap += (s, e) => writeLog(string.Format("Removed Lap. Number={0}", e.Lap != null ? e.Lap.LapNumber : -1));
            manager.ReturnedToGarage += (s, e) => writeLog("Now in Garage");
            manager.FinishedOutLap += (s, e) => writeLog("Completed Out Lap");
            manager.StartedOutLap += (s, e) => writeLog("Started Out Lap");
            manager.SetFastestLap += (s, e) => writeLog(string.Format("Set Fastest Lap={0}", e.Lap.LapTime.AsTimeString()));
            manager.PacketProcessed += (s, e) => writeLog(string.Format("Packet: {0}", e.Packet));
            
#else
            this.Height = 670;
            cboField.Visible = false;
            label8.Visible = false;
            LogBox.Visible = false;
#endif
                
            writeLog("Listing on port " + PORTNUM + " for connections from " +
                         (F1SpeedSettings.AllowConnectionsFromOtherMachines ? "ANY IP" : IP));
            
            remoteIP = F1SpeedSettings.AllowConnectionsFromOtherMachines ? new IPEndPoint(IPAddress.Any, PORTNUM) : new IPEndPoint(IPAddress.Parse(IP), PORTNUM);

            foreach(var ctrl in this.Controls)
            {
                var grpBox = ctrl as GroupBox;
                if (grpBox == null) continue;
                if (grpBox.Name == "ControlsGroup") continue;
                grpBox.Paint += new PaintEventHandler(grpBox_Paint);
            }

            // Set up the socket for collecting game telemetry
            try
            {
                udpSocket = new UdpClient();
                udpSocket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udpSocket.ExclusiveAddressUse = false;
                udpSocket.Client.Bind(remoteIP);
                writeLog("Bound to socket on " + IP + ":" + PORTNUM.ToString());
                StartListening();
            }
            catch (Exception error)
            {
                writeLog(error.ToString());
                // throw;
            }
        }
        
        void grpBox_Paint(object sender, PaintEventArgs e)
        {
            var box = sender as GroupBox;
            e.Graphics.Clear(Color.Black);
        }

        // Connect to the game.  This doesn't really connect (because UDP is a connectionless
        // protocol - it just kicks off the data collection thread to receive
        // the UDP packets and the timer for refreshing the screen display.
        private void StartListening()
        {
            if (telemCaptureThread == null)
            {
                // Kick off a thread to start collecting data
                telemCaptureThread = new Thread(FetchData);
                telemCaptureThread.Start();
                writeLog("Starting capture...");

                // Kick off the timer to control screen updates
                timer1.Interval = TIMERINTERVAL_MS;
                timer1.Enabled = true;
                timer1.Start();
            }
        }

        // This method runs continously in the data collection thread.  It
        // waits to receive UDP packets from the game, converts them and writes
        // them to the shared struct variable.
        private void FetchData()
        {
            while (true)
            {
                // Get the data (this will block until we get a packet)
                Byte[] receiveBytes = udpSocket.Receive(ref senderIP);

                // Lock access to the shared struct
                syncMutex.WaitOne();

                TransmissionLabel.BackColor = Color.Green;

                // Convert the bytes received to the shared struct
                latestData = PacketUtilities.ConvertToPacket(receiveBytes);
                manager.ProcessIncomingPacket(latestData);

                //TransmissionLabel.BackColor = Color.Red;

                // Release the lock again
                syncMutex.ReleaseMutex();
            }
        }

        // Method that periodically (per invocations through the timer) updates
        // the displayed telemetry
        private void UpdateDisplay(Object myObject, EventArgs myEventArgs)
        {
            // Suspend the timer
            timer1.Stop();

            // Wait for mutex lock
            syncMutex.WaitOne();

            if (!string.IsNullOrEmpty(cboField.SelectedItem.ToString()))
            {
                FieldValueLabel.Text = manager.GetCurrentData(cboField.SelectedItem.ToString());
            }

            // Update the display
            var delta = manager.GetSpeedDelta();
            DeltaLabel.Text = delta;
            if (delta != "--")
            {
                DeltaLabel.ForeColor = delta.Substring(0, 1) == "+" ? Color.Green : Color.Red;
            }
            else
            {
                DeltaLabel.ForeColor = Color.White;
            }

            ComparisonModeLabel.Text = manager.ComparisonMode == ComparisonModeEnum.Reference ? "Reference Lap" : "Fastest Lap";
            ComparisonLapLabel.Text = manager.ComparisonLapTime;
            CurrentLapLabel.Text = manager.CurrentLapTime;
            LastLapLabel.Text = manager.LastLapTime;

            UpdateTimeDelta(manager.GetTimeDelta());
            UpdateThrottleBrake(manager.CurrentThrottle, manager.CurrentBrake);
            UpdateWheelSpin(manager.CurrentWheelSpin(WheelspinWheel.FrontLeft), manager.CurrentWheelSpin(WheelspinWheel.FrontRight), manager.CurrentWheelSpin(WheelspinWheel.RearLeft), manager.CurrentWheelSpin(WheelspinWheel.RearRight));
            
            UpdateSectors();

            TransmissionLabel.BackColor = Color.Transparent;


            // Release the lock
            syncMutex.ReleaseMutex();

            Application.DoEvents();

            // Restart the timer
            timer1.Start();
        }

        public void UpdateSectors()
        {             
            SetSector1Time(manager.Sector1.FastestTime, manager.Sector1.CurrentTime, manager.Sector1.DeltaTime);            
            SetSector2Time(manager.Sector2.FastestTime, manager.Sector2.CurrentTime, manager.Sector2.DeltaTime);            
            SetSector3Time(manager.Sector3.FastestTime, manager.Sector3.CurrentTime, manager.Sector3.DeltaTime);                        
        }

        public void SetSector1Time(float fastest, float current, float delta)
        {            
            // negative time means a new fastest sector!            
            var newLapIsFaster = current < fastest;
            var foreColor = newLapIsFaster ? Color.Green : Color.Red;

            Sector1DeltaSplit.Text = delta.AsGapString(true);
            Sector1CurrentSplit.Text = current.AsTimeString(true);
            Sector1FastestSplit.Text = fastest.AsTimeString(true);

            Sector1Label.ForeColor = foreColor;
            Sector1CurrentSplit.ForeColor = foreColor;
            Sector1DeltaSplit.ForeColor = foreColor;
            Sector1FastestSplit.ForeColor = foreColor;
        }

        public void SetSector2Time(float fastest, float current, float delta)
        {
            // negative time means a new fastest sector!            
            var newLapIsFaster = current < fastest;
            var foreColor = newLapIsFaster ? Color.Green : Color.Red;

            Sector2DeltaSplit.Text = delta.AsGapString(true);
            Sector2CurrentSplit.Text = current.AsTimeString(true);
            Sector2FastestSplit.Text = fastest.AsTimeString(true);

            Sector2Label.ForeColor = foreColor;
            Sector2CurrentSplit.ForeColor = foreColor;
            Sector2DeltaSplit.ForeColor = foreColor;
            Sector2FastestSplit.ForeColor = foreColor;
        }

        public void SetSector3Time(float fastest, float current, float delta)
        {
            // negative time means a new fastest sector!            
            var newLapIsFaster = current < fastest;
            var foreColor = newLapIsFaster ? Color.Green : Color.Red;

            Sector3DeltaSplit.Text = delta.AsGapString(true);
            Sector3CurrentSplit.Text = current.AsTimeString(true);
            Sector3FastestSplit.Text = fastest.AsTimeString(true);

            Sector3Label.ForeColor = foreColor;
            Sector3CurrentSplit.ForeColor = foreColor;
            Sector3DeltaSplit.ForeColor = foreColor;
            Sector3FastestSplit.ForeColor = foreColor;
        }

        public void ResetSectorTiming()
        {
            var foreColor = Color.White;

            Sector1Label.ForeColor = foreColor;
            Sector1CurrentSplit.ForeColor = foreColor;
            Sector1DeltaSplit.ForeColor = foreColor;
            Sector1FastestSplit.ForeColor = foreColor;

            Sector2Label.ForeColor = foreColor;
            Sector2CurrentSplit.ForeColor = foreColor;
            Sector2DeltaSplit.ForeColor = foreColor;
            Sector2FastestSplit.ForeColor = foreColor;

            Sector3Label.ForeColor = foreColor;
            Sector3CurrentSplit.ForeColor = foreColor;
            Sector3DeltaSplit.ForeColor = foreColor;
            Sector3FastestSplit.ForeColor = foreColor;

            Sector1CurrentSplit.Text = 0f.AsGapString(true);
            Sector1DeltaSplit.Text = "";
            Sector1FastestSplit.Text = 0f.AsGapString(true);

            Sector2CurrentSplit.Text = 0f.AsGapString(true);
            Sector2DeltaSplit.Text = "";
            Sector2FastestSplit.Text = 0f.AsGapString(true);

            Sector3CurrentSplit.Text = 0f.AsGapString(true);
            Sector3DeltaSplit.Text = "";
            Sector3FastestSplit.Text = 0f.AsGapString(true);
        }

       
        private void UpdateThrottleBrake(float throttle, float brake)
        {
            const float MaxHeight = 75;

            BrakeBar.Height = (int)(MaxHeight * brake);
            ThrottleBar.Height = (int)(MaxHeight * throttle);

            BrakeBar.Location = new Point(21, 81 - BrakeBar.Height);
            ThrottleBar.Location = new Point(88, 81 - ThrottleBar.Height);

            if (brake > 0.99)
            {
                BrakeBar.BackColor = Color.White;
            }
            else
                BrakeBar.BackColor = Color.Red;
        }

        private void UpdateWheelSpin(float fl, float fr, float rl, float rr)
        {
            const float MaxHeight = 75;
            const float scale = 20.0f;

            /* normalize wheel speeds */
            fl = Math.Min(Math.Abs(fl / scale), 1);
            fr = Math.Min(Math.Abs(fr / scale), 1);
            rl = Math.Min(Math.Abs(rl / scale), 1);
            rr = Math.Min(Math.Abs(rr / scale), 1);

            FrontLeftWheelSpin.Height = (int)(MaxHeight * fl);
            FrontRightWheelSpin.Height = (int)(MaxHeight * fr);
            BackLeftWheelSpin.Height = (int)(MaxHeight * rl);
            BackRightWheelSpin.Height = (int)(MaxHeight * rr);

            FrontLeftWheelSpin.Location = new Point(FrontLeftWheelSpin.Location.X, 75 + 106 - FrontLeftWheelSpin.Height);
            FrontRightWheelSpin.Location = new Point(FrontRightWheelSpin.Location.X, 75 + 106 - FrontRightWheelSpin.Height);
            BackLeftWheelSpin.Location = new Point(BackLeftWheelSpin.Location.X, 75 + 225 - BackLeftWheelSpin.Height);
            BackRightWheelSpin.Location = new Point(BackRightWheelSpin.Location.X, 75 + 225 - BackRightWheelSpin.Height);
        }


        private void UpdateTimeDelta(float timeDelta)
        {
            TimeDeltaLabel.Text = timeDelta == 0
                                      ? "0.0"
                                      : (timeDelta > 0 ? "+" : "") + string.Format("{0:0.00}", timeDelta) + "s";

            FasterBarLabel.Width = 0;
            SlowerBarLabel.Width = 0;

            const int MaxWidth = 330;
            const float MaxTimeDelta = 2f;

            Size newSize;

            if (Math.Abs(timeDelta) >= 10f)
            {
                newSize = new Size(MaxWidth, 37);
            }
            else
            {
                newSize = new Size((int)(Math.Abs(timeDelta) / MaxTimeDelta * MaxWidth), 37);
            }

            if (timeDelta > 0)
            {
                FasterBarLabel.Size = newSize;
                FasterBarLabel.Left = BarGroup.Width / 2;
            }
            if (timeDelta < 0)
            {
                SlowerBarLabel.Size = newSize;
                SlowerBarLabel.Left = BarGroup.Width / 2 - newSize.Width;
            }
        }

        private void writeLog(string log)
        {
            if (this.LogBox.InvokeRequired)
            {
                this.BeginInvoke(new WriteLogCallback(writeLog), new object[] { log });
            }
            else
            {
                LogBox.Items.Insert(0, log);
                if (LogBox.Items.Count > 100)
                    LogBox.Items.RemoveAt(LogBox.Items.Count - 1);

                logger.Info(log);
            }
        }

        private void UpdateCircuitName(string circuitName, string lapType)
        {
            if (this.CircuitLabel.InvokeRequired)
                this.BeginInvoke(new UpdateCircuitCallback(UpdateCircuitName), new object[] {circuitName, lapType});
            else
            {
                CircuitLabel.Text = circuitName;
                LapTypeLabel.Text = lapType;
            }
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            var version = Application.ProductVersion;
            if (System.Deployment.Application.ApplicationDeployment.IsNetworkDeployed)
            {
                var deploy = System.Deployment.Application.ApplicationDeployment.CurrentDeployment;
                version = deploy.CurrentVersion.ToString();
            }
            this.Text = string.Format("F1 Speed (v{0})", version);

            TransmissionLabel.BackColor = Color.Red;

            //SetWindowPos(this.Handle, HWND_TOPMOST, 0, 0, 0, 0, TOPMOST_FLAGS);            
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            udpSocket.Close();
            if (telemCaptureThread != null)
            {
                telemCaptureThread.Abort();
                telemCaptureThread = null;
            }
            timer1.Stop();
            Application.Exit();
        }
        
        private void helpToolStripMenuItem_Click(object sender, EventArgs e)
        {
            System.Diagnostics.Process.Start("http://www.f1speedguides.com/f1speed/");
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void exportFastestLapToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (manager.FastestLap == null)
            {
                MessageBox.Show("No fastest lap to export! Please set a fastest lap first :)", "Doh!",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var saveDialog = new SaveFileDialog
            {
                Filter = "F1Speed|*.f1s",
                Title = "Export Fastest Lap",
                FileName = manager.FastestLap.CircuitName + "_" + manager.FastestLap.LapType + ".f1s"
            };
            var result = saveDialog.ShowDialog();

            if (result == DialogResult.OK && !string.IsNullOrEmpty(saveDialog.FileName))
            {
                var fileRepo = new BinaryTelemetryLapRepository();
                fileRepo.Save(manager.FastestLap, saveDialog.FileName);
            }
        }

        private void importCompareLapToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var openDialog = new OpenFileDialog
            {
                Filter = "F1Speed|*.f1s",
                Title = "Import Reference Lap",
                Multiselect = false
            };
            var result = openDialog.ShowDialog();

            if (result == DialogResult.OK && !string.IsNullOrEmpty(openDialog.FileName))
            {
                var fileRepo = new BinaryTelemetryLapRepository();

                var lap = fileRepo.Get(openDialog.FileName);
                if (lap != null)
                {
                    manager.SetReferenceLap(lap);
                    CircuitLabel.Text = manager.ReferenceLap.Circuit.Name;
                    LapTypeLabel.Text = manager.ReferenceLap.LapType;
                    
                    clearReferenceLapToolStripMenuItem.Enabled = true;
                }
            }
        }

        private void clearReferenceLapToolStripMenuItem_Click(object sender, EventArgs e)
        {
            manager.ClearReferenceLap();
            clearReferenceLapToolStripMenuItem.Enabled = false;
        }

        private void exportFastestLapToF1PerfViewToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (manager.FastestLap == null)
            {
                MessageBox.Show("No fastest lap to export! Please set a fastest lap first :)", "Doh!",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var saveDialog = new SaveFileDialog
            {
                Filter = "F1PerfView|*.csv",
                Title = "Export Fastest Lap to F1PerfView",
                FileName =
                    manager.FastestLap.CircuitName + "_" +
                    manager.FastestLap.LapTime.ToString("0.000") + ".csv"
            };
            var result = saveDialog.ShowDialog();

            if (result == DialogResult.OK && !string.IsNullOrEmpty(saveDialog.FileName))
            {
                var repository = new F1PerfViewTelemetryLapRepository();
                repository.Save(manager.FastestLap, saveDialog.FileName);
            }
        }

        private void optionsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var optionsForm = new OptionsForm();
            optionsForm.ShowDialog(this);
        }

      
    }
}
