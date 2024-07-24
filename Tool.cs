using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using System.Text.Json;
using static System.Windows.Forms.AxHost;

namespace Hacktice
{
    public partial class Tool : Form
    {
        enum State
        {
            INVALIDATED,
            EMULATOR,
            ROM,
            CORRUPTED,
            RUNNING,
        };

        System.Threading.Timer _timer;

        // Access from '_timer' thread only
        State _stateValue = State.INVALIDATED;
        readonly Emulator _emulator = new Emulator();
        Config _lastSeenEmulatorConfig = new Config();
        bool _canWriteToEmu = false;

        private State EmulatorState
        {
            get { return _stateValue; }
            set { var oldValue = _stateValue; _stateValue = value; if (oldValue != _stateValue) { UpdateEmulatorState(); } }
        }

        // Used from UI thread to avoid event loops
        bool _muteConfigEvents = false;

        // Whenever user wants to do anything with emulator, these vars are set
        // These are shared between UI & timer_ thread. 
        // This is incredibly ugly but I do not know other way to do it in C# as dispatch queues can do
        // Might want to use 'Interlocked' access because of multithreading, be careful
        volatile Config _wantToUpdateConfig;
        volatile Config _config;

        private Config NeedToUpdateConfig
        {
            get { return Interlocked.Exchange(ref _wantToUpdateConfig, null); }
        }

        class MuteScope : IDisposable
        {
            readonly Tool _tool;

            public MuteScope(Tool tool)
            {
                _tool = tool;
                _tool._muteConfigEvents = true;
            }
            public void Dispose()
            {
                _tool._muteConfigEvents = false;
            }
        }

        const string DEFAULT_CONFIG_NAME = "feud_config.json";

        public Tool()
        {
            InitializeComponent();
            using (MuteScope mute = new MuteScope(this))
            {
                // -
            }

            try
            {
                var path = Path.Combine(Application.LocalUserAppDataPath, DEFAULT_CONFIG_NAME);
                using (var reader = new FileStream(path, FileMode.Open))
                {
                    UpdateUIFromConfig(JsonSerializer.Deserialize<JConfig>(reader));
                }
            }
            catch (Exception)
            { }

            _config = MakeConfig();
        }

        ~Tool()
        {
            if (!(_timer is object))
            {
                return;
            }

            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            WaitHandle handle = new AutoResetEvent(false);
            _timer.Dispose(handle);
            handle.WaitOne();
            _timer.Dispose();
        }

        // Call from other thread for safe UI invoke
        private void SafeInvoke(MethodInvoker updater, bool forceSynchronous = false)
        {
            if (InvokeRequired)
            {
                if (forceSynchronous)
                {
                    Invoke((MethodInvoker)delegate { SafeInvoke(updater, forceSynchronous); });
                }
                else
                {
                    BeginInvoke((MethodInvoker)delegate { SafeInvoke(updater, forceSynchronous); });
                }
            }
            else
            {
                if (IsDisposed)
                {
                    throw new ObjectDisposedException("Control is already disposed.");
                }

                updater();
            }
        }

        private string GetStateString()
        {
            switch (_stateValue)
            {
                case State.INVALIDATED:
                    return "No supported emulator is running.";
                case State.EMULATOR:
                    return "Emulator is running but no running ROM found";
                case State.ROM:
                    return "ROM is found but it is not Family Feud";
                case State.RUNNING:
                    return $"Family Feud is running";
            }

            return "Corrupted";
        }

        private Color GetStateColor()
        {
            switch (_stateValue)
            {
                case State.INVALIDATED:
                    return Color.DarkGray;
                case State.EMULATOR:
                    return Color.MediumPurple;
                case State.ROM:
                    return Color.DarkKhaki;
                case State.RUNNING:
                    return Color.Green;
            }

            return Color.Red;
        }

        private void UpdateEmulatorState()
        {
            var state = GetStateString();
            var color = GetStateColor();
            bool canUseConfig = _stateValue >= State.RUNNING;
            if (!canUseConfig)
            {
                _lastSeenEmulatorConfig = null;
            }

            SafeInvoke(delegate {
                pictureBoxState.BackColor = color;
                labelEmulatorState.Text = state;
            });
        }

        private void PrepareHacktice()
        {
            // 0C0134C0 00000000
            State newState = State.ROM;
            try
            {
                if (!_emulator.RefreshHacktice())
                {
                    _lastSeenEmulatorConfig = null;
                    return;
                }

                newState = State.RUNNING;
                return;
            }
            finally
            {
                EmulatorState = newState;
            }
        }

        private bool IsEmulatorReady()
        {
            return EmulatorState > State.ROM && _emulator.Ok();
        }

        private List<string> ResolvePrintStrings(uint[] prints)
        {
            List<string> resolvedPrints = new List<string>();
            foreach (uint print in prints)
            {
                if (print == 0 || ((print & 0x80000000) == 0) || ((print & 3) != 0))
                    break;

                try
                {
                    var strBytes = _emulator.ReadBytes(print, 32);
                    resolvedPrints.Add(Config.BytesToText(strBytes));
                }
                catch (Exception)
                { }
            }

            return resolvedPrints;
        }

        private void EmulatorStateUpdate(object state)
        {
            var userUpdatedConfig = NeedToUpdateConfig;

            try
            {
                if (!IsEmulatorReady())
                {
                    _lastSeenEmulatorConfig = null;
                    var res = _emulator.Prepare();
                    switch (res)
                    {
                        case Emulator.PrepareResult.NOT_FOUND:
                            EmulatorState = State.INVALIDATED;
                            break;
                        case Emulator.PrepareResult.ONLY_EMULATOR:
                            EmulatorState = State.EMULATOR;
                            break;
                        case Emulator.PrepareResult.OK:
                            EmulatorState = State.ROM;
                            break;
                    }
                }

                if (EmulatorState >= State.ROM)
                {
                    PrepareHacktice();
                }

                if (EmulatorState >= State.RUNNING)
                {
                    // TODO: This logic gets complicated, separate this away
                    // the first time emulator is good
                    if (!(_lastSeenEmulatorConfig is object))
                    {
                        _lastSeenEmulatorConfig = _emulator.ReadConfig();
                        var cfg = _lastSeenEmulatorConfig;
                        List<string> resolvedPrints = ResolvePrintStrings(cfg.state.prints);
                        SafeInvoke(delegate {
                            UpdateUIFromConfig(cfg);
                            UpdateUIFromState(cfg.state, resolvedPrints.ToArray());
                        });
                    }

                    if (userUpdatedConfig is object)
                    {
                        _lastSeenEmulatorConfig = userUpdatedConfig;
                        if (_canWriteToEmu)
                            _emulator.Write(userUpdatedConfig);
                    }

                    var currentEmuConfig = _emulator.ReadState();
                    if (!_lastSeenEmulatorConfig.Equal(currentEmuConfig))
                    {
                        List<string> resolvedPrints = ResolvePrintStrings(currentEmuConfig.prints);
                        SafeInvoke(delegate {
                            UpdateUIFromState(currentEmuConfig, resolvedPrints.ToArray());
                        });
                        _lastSeenEmulatorConfig.state = currentEmuConfig;
                    }
                }
            }
            catch (Exception)
            {
                EmulatorState = State.INVALIDATED;
            }

            bool ready = IsEmulatorReady();
            SafeInvoke(delegate {
                if (_timer is object)
                    _timer.Change(ready ? 30 : 1000, Timeout.Infinite);
            });
        }

        private void buttonPatch_Click(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "ROMs (*.z64)|*.z64|All files (*.*)|*.*",
                FilterIndex = 1,
            };

            if (openFileDialog.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    var path = openFileDialog.FileName;
                    var rom = File.ReadAllBytes(path);
                    Patcher patcher = new Patcher(rom);
                    patcher.WriteConfig(MakeConfig());
                    patcher.Save(path);
                    MessageBox.Show("Patch applied successfully!", "hacktice", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to patch: {ex.Message}", "hacktice", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }
        }

        string paddedString(string str, int sz)
        {
            if (str.Length > sz)
                str = str.Substring(0, sz);

            while (str.Length < sz)
            {
                str = "0" + str;
            }

            return str;
        }

        byte[] paddedStringToByte(string str, int sz)
        {
            return Config.TextToBytes(paddedString(str, sz), 4);
        }

        private Config MakeConfig()
        {
            // oh no
            var cfg = new Config();
            cfg.header = Canary.Magic;
            cfg.state.curRound = new byte[4] { 0, 0, 0, (byte)textBoxRoundNumber.Text[0] };
            cfg.state.prints = new uint[11];
            cfg.state.internalState = Convert.ToInt32(textBoxState.Text);
            int score = Convert.ToInt32(textBoxScore.Text);
            cfg.state.pendingScore = score < 1000 ? score : 999;
            cfg.state.scores = new Score[2];
            cfg.state.scores[0].score = paddedStringToByte(textBoxTeam1Score.Text, 3);
            cfg.state.scores[1].score = paddedStringToByte(textBoxTeam2Score.Text, 3);

            cfg.teams = new Team[2];
            cfg.teams[0].teamName = Config.TextToBytes(textBoxTeam1Name.Text, 16);
            cfg.teams[0].players = new Player[5];
            cfg.teams[0].players[0].name = Config.TextToBytes(textBox1Player1.Text, 32);
            cfg.teams[0].players[1].name = Config.TextToBytes(textBox1Player2.Text, 32);
            cfg.teams[0].players[2].name = Config.TextToBytes(textBox1Player3.Text, 32);
            cfg.teams[0].players[3].name = Config.TextToBytes(textBox1Player4.Text, 32);
            cfg.teams[0].players[4].name = Config.TextToBytes(textBox1Player5.Text, 32);

            cfg.teams[1].teamName = Config.TextToBytes(textBoxTeam2Name.Text, 16);
            cfg.teams[1].players = new Player[5];
            cfg.teams[1].players[0].name = Config.TextToBytes(textBox2Player1.Text, 32);
            cfg.teams[1].players[1].name = Config.TextToBytes(textBox2Player2.Text, 32);
            cfg.teams[1].players[2].name = Config.TextToBytes(textBox2Player3.Text, 32);
            cfg.teams[1].players[3].name = Config.TextToBytes(textBox2Player4.Text, 32);
            cfg.teams[1].players[4].name = Config.TextToBytes(textBox2Player5.Text, 32);

            cfg.rounds = new Round[5];
            cfg.rounds[0].answers = new Answer[8];
            cfg.rounds[0].answers[0].name = Config.TextToBytes(textBoxRound1Answer1.Text, 28);
            cfg.rounds[0].answers[0].cost = paddedStringToByte(textBoxRound1Answer1Score.Text, 2);
            cfg.rounds[0].answers[1].name = Config.TextToBytes(textBoxRound1Answer2.Text, 28);
            cfg.rounds[0].answers[1].cost = paddedStringToByte(textBoxRound1Answer2Score.Text, 2);
            cfg.rounds[0].answers[2].name = Config.TextToBytes(textBoxRound1Answer3.Text, 28);
            cfg.rounds[0].answers[2].cost = paddedStringToByte(textBoxRound1Answer3Score.Text, 2);
            cfg.rounds[0].answers[3].name = Config.TextToBytes(textBoxRound1Answer4.Text, 28);
            cfg.rounds[0].answers[3].cost = paddedStringToByte(textBoxRound1Answer4Score.Text, 2);
            cfg.rounds[0].answers[4].name = Config.TextToBytes(textBoxRound1Answer5.Text, 28);
            cfg.rounds[0].answers[4].cost = paddedStringToByte(textBoxRound1Answer5Score.Text, 2);
            cfg.rounds[0].answers[5].name = Config.TextToBytes(textBoxRound1Answer6.Text, 28);
            cfg.rounds[0].answers[5].cost = paddedStringToByte(textBoxRound1Answer6Score.Text, 2);
            cfg.rounds[0].answers[6].name = Config.TextToBytes(textBoxRound1Answer7.Text, 28);
            cfg.rounds[0].answers[6].cost = paddedStringToByte(textBoxRound1Answer7Score.Text, 2);
            cfg.rounds[0].answers[7].name = Config.TextToBytes(textBoxRound1Answer8.Text, 28);
            cfg.rounds[0].answers[7].cost = paddedStringToByte(textBoxRound1Answer8Score.Text, 2);

            cfg.rounds[1].answers = new Answer[8];
            cfg.rounds[1].answers[0].name = Config.TextToBytes(textBoxRound2Answer1.Text, 28);
            cfg.rounds[1].answers[0].cost = paddedStringToByte(textBoxRound2Answer1Score.Text, 2);
            cfg.rounds[1].answers[1].name = Config.TextToBytes(textBoxRound2Answer2.Text, 28);
            cfg.rounds[1].answers[1].cost = paddedStringToByte(textBoxRound2Answer2Score.Text, 2);
            cfg.rounds[1].answers[2].name = Config.TextToBytes(textBoxRound2Answer3.Text, 28);
            cfg.rounds[1].answers[2].cost = paddedStringToByte(textBoxRound2Answer3Score.Text, 2);
            cfg.rounds[1].answers[3].name = Config.TextToBytes(textBoxRound2Answer4.Text, 28);
            cfg.rounds[1].answers[3].cost = paddedStringToByte(textBoxRound2Answer4Score.Text, 2);
            cfg.rounds[1].answers[4].name = Config.TextToBytes(textBoxRound2Answer5.Text, 28);
            cfg.rounds[1].answers[4].cost = paddedStringToByte(textBoxRound2Answer5Score.Text, 2);
            cfg.rounds[1].answers[5].name = Config.TextToBytes(textBoxRound2Answer6.Text, 28);
            cfg.rounds[1].answers[5].cost = paddedStringToByte(textBoxRound2Answer6Score.Text, 2);
            cfg.rounds[1].answers[6].name = Config.TextToBytes(textBoxRound2Answer7.Text, 28);
            cfg.rounds[1].answers[6].cost = paddedStringToByte(textBoxRound2Answer7Score.Text, 2);
            cfg.rounds[1].answers[7].name = Config.TextToBytes(textBoxRound2Answer8.Text, 28);
            cfg.rounds[1].answers[7].cost = paddedStringToByte(textBoxRound2Answer8Score.Text, 2);

            cfg.rounds[2].answers = new Answer[8];
            cfg.rounds[2].answers[0].name = Config.TextToBytes(textBoxRound3Answer1.Text, 28);
            cfg.rounds[2].answers[0].cost = paddedStringToByte(textBoxRound3Answer1Score.Text, 2);
            cfg.rounds[2].answers[1].name = Config.TextToBytes(textBoxRound3Answer2.Text, 28);
            cfg.rounds[2].answers[1].cost = paddedStringToByte(textBoxRound3Answer2Score.Text, 2);
            cfg.rounds[2].answers[2].name = Config.TextToBytes(textBoxRound3Answer3.Text, 28);
            cfg.rounds[2].answers[2].cost = paddedStringToByte(textBoxRound3Answer3Score.Text, 2);
            cfg.rounds[2].answers[3].name = Config.TextToBytes(textBoxRound3Answer4.Text, 28);
            cfg.rounds[2].answers[3].cost = paddedStringToByte(textBoxRound3Answer4Score.Text, 2);
            cfg.rounds[2].answers[4].name = Config.TextToBytes(textBoxRound3Answer5.Text, 28);
            cfg.rounds[2].answers[4].cost = paddedStringToByte(textBoxRound3Answer5Score.Text, 2);
            cfg.rounds[2].answers[5].name = Config.TextToBytes(textBoxRound3Answer6.Text, 28);
            cfg.rounds[2].answers[5].cost = paddedStringToByte(textBoxRound3Answer6Score.Text, 2);
            cfg.rounds[2].answers[6].name = Config.TextToBytes(textBoxRound3Answer7.Text, 28);
            cfg.rounds[2].answers[6].cost = paddedStringToByte(textBoxRound3Answer7Score.Text, 2);
            cfg.rounds[2].answers[7].name = Config.TextToBytes(textBoxRound3Answer8.Text, 28);
            cfg.rounds[2].answers[7].cost = paddedStringToByte(textBoxRound3Answer8Score.Text, 2);

            cfg.rounds[3].answers = new Answer[8];
            cfg.rounds[3].answers[0].name = Config.TextToBytes(textBoxRound4Answer1.Text, 28);
            cfg.rounds[3].answers[0].cost = paddedStringToByte(textBoxRound4Answer1Score.Text, 2);
            cfg.rounds[3].answers[1].name = Config.TextToBytes(textBoxRound4Answer2.Text, 28);
            cfg.rounds[3].answers[1].cost = paddedStringToByte(textBoxRound4Answer2Score.Text, 2);
            cfg.rounds[3].answers[2].name = Config.TextToBytes(textBoxRound4Answer3.Text, 28);
            cfg.rounds[3].answers[2].cost = paddedStringToByte(textBoxRound4Answer3Score.Text, 2);
            cfg.rounds[3].answers[3].name = Config.TextToBytes(textBoxRound4Answer4.Text, 28);
            cfg.rounds[3].answers[3].cost = paddedStringToByte(textBoxRound4Answer4Score.Text, 2);
            cfg.rounds[3].answers[4].name = Config.TextToBytes(textBoxRound4Answer5.Text, 28);
            cfg.rounds[3].answers[4].cost = paddedStringToByte(textBoxRound4Answer5Score.Text, 2);
            cfg.rounds[3].answers[5].name = Config.TextToBytes(textBoxRound4Answer6.Text, 28);
            cfg.rounds[3].answers[5].cost = paddedStringToByte(textBoxRound4Answer6Score.Text, 2);
            cfg.rounds[3].answers[6].name = Config.TextToBytes(textBoxRound4Answer7.Text, 28);
            cfg.rounds[3].answers[6].cost = paddedStringToByte(textBoxRound4Answer7Score.Text, 2);
            cfg.rounds[3].answers[7].name = Config.TextToBytes(textBoxRound4Answer8.Text, 28);
            cfg.rounds[3].answers[7].cost = paddedStringToByte(textBoxRound4Answer8Score.Text, 2);

            cfg.rounds[4].answers = new Answer[8];
            cfg.rounds[4].answers[0].name = Config.TextToBytes(textBoxRound5Answer1.Text, 28);
            cfg.rounds[4].answers[0].cost = paddedStringToByte(textBoxRound5Answer1Score.Text, 2);
            cfg.rounds[4].answers[1].name = Config.TextToBytes(textBoxRound5Answer2.Text, 28);
            cfg.rounds[4].answers[1].cost = paddedStringToByte(textBoxRound5Answer2Score.Text, 2);
            cfg.rounds[4].answers[2].name = Config.TextToBytes(textBoxRound5Answer3.Text, 28);
            cfg.rounds[4].answers[2].cost = paddedStringToByte(textBoxRound5Answer3Score.Text, 2);
            cfg.rounds[4].answers[3].name = Config.TextToBytes(textBoxRound5Answer4.Text, 28);
            cfg.rounds[4].answers[3].cost = paddedStringToByte(textBoxRound5Answer4Score.Text, 2);
            cfg.rounds[4].answers[4].name = Config.TextToBytes(textBoxRound5Answer5.Text, 28);
            cfg.rounds[4].answers[4].cost = paddedStringToByte(textBoxRound5Answer5Score.Text, 2);
            cfg.rounds[4].answers[5].name = Config.TextToBytes(textBoxRound5Answer6.Text, 28);
            cfg.rounds[4].answers[5].cost = paddedStringToByte(textBoxRound5Answer6Score.Text, 2);
            cfg.rounds[4].answers[6].name = Config.TextToBytes(textBoxRound5Answer7.Text, 28);
            cfg.rounds[4].answers[6].cost = paddedStringToByte(textBoxRound5Answer7Score.Text, 2);
            cfg.rounds[4].answers[7].name = Config.TextToBytes(textBoxRound5Answer8.Text, 28);
            cfg.rounds[4].answers[7].cost = paddedStringToByte(textBoxRound5Answer8Score.Text, 2);

            cfg.final.answersInit = new Answer[5];
            cfg.final.answersInit[0].name = Config.TextToBytes(textBoxFinalPreAnswer1.Text, 28);
            cfg.final.answersInit[0].cost = paddedStringToByte(textBoxFinalPreAnswer1Score.Text, 2);
            cfg.final.answersInit[1].name = Config.TextToBytes(textBoxFinalPreAnswer2.Text, 28);
            cfg.final.answersInit[1].cost = paddedStringToByte(textBoxFinalPreAnswer2Score.Text, 2);
            cfg.final.answersInit[2].name = Config.TextToBytes(textBoxFinalPreAnswer3.Text, 28);
            cfg.final.answersInit[2].cost = paddedStringToByte(textBoxFinalPreAnswer3Score.Text, 2);
            cfg.final.answersInit[3].name = Config.TextToBytes(textBoxFinalPreAnswer4.Text, 28);
            cfg.final.answersInit[3].cost = paddedStringToByte(textBoxFinalPreAnswer4Score.Text, 2);
            cfg.final.answersInit[4].name = Config.TextToBytes(textBoxFinalPreAnswer5.Text, 28);
            cfg.final.answersInit[4].cost = paddedStringToByte(textBoxFinalPreAnswer5Score.Text, 2);

            cfg.final.answersAfter = new Answer[5];
            cfg.final.answersAfter[0].name = Config.TextToBytes(textBoxFinalPostAnswer1.Text, 28);
            cfg.final.answersAfter[0].cost = paddedStringToByte(textBoxFinalPostAnswer1Score.Text, 2);
            cfg.final.answersAfter[1].name = Config.TextToBytes(textBoxFinalPostAnswer2.Text, 28);
            cfg.final.answersAfter[1].cost = paddedStringToByte(textBoxFinalPostAnswer2Score.Text, 2);
            cfg.final.answersAfter[2].name = Config.TextToBytes(textBoxFinalPostAnswer3.Text, 28);
            cfg.final.answersAfter[2].cost = paddedStringToByte(textBoxFinalPostAnswer3Score.Text, 2);
            cfg.final.answersAfter[3].name = Config.TextToBytes(textBoxFinalPostAnswer4.Text, 28);
            cfg.final.answersAfter[3].cost = paddedStringToByte(textBoxFinalPostAnswer4Score.Text, 2);
            cfg.final.answersAfter[4].name = Config.TextToBytes(textBoxFinalPostAnswer5.Text, 28);
            cfg.final.answersAfter[4].cost = paddedStringToByte(textBoxFinalPostAnswer5Score.Text, 2);

            return cfg;
        }

        static string textCut(string name, int size)
        {
            if (name.Length > size)
                return name.Substring(0, size);
            else
                return name;
        }

        private JConfig MakeJConfig()
        {
            // oh no
            var cfg = new JConfig();
            cfg.state = new JStatus();
            int score = Convert.ToInt32(textBoxScore.Text);
            cfg.state.scores = new JScore[2];
            cfg.state.scores[0] = new JScore();
            cfg.state.scores[0].score = paddedString(textBoxTeam1Score.Text, 3);
            cfg.state.scores[1] = new JScore();
            cfg.state.scores[1].score = paddedString(textBoxTeam2Score.Text, 3);

            cfg.teams = new JTeam[2];
            cfg.teams[0] = new JTeam();
            cfg.teams[0].teamName = textCut(textBoxTeam1Name.Text, 16);
            cfg.teams[0].players = new JPlayer[5];
            cfg.teams[0].players[0] = new JPlayer();
            cfg.teams[0].players[0].name = textCut(textBox1Player1.Text, 32);
            cfg.teams[0].players[1] = new JPlayer();
            cfg.teams[0].players[1].name = textCut(textBox1Player2.Text, 32);
            cfg.teams[0].players[2] = new JPlayer();
            cfg.teams[0].players[2].name = textCut(textBox1Player3.Text, 32);
            cfg.teams[0].players[3] = new JPlayer();
            cfg.teams[0].players[3].name = textCut(textBox1Player4.Text, 32);
            cfg.teams[0].players[4] = new JPlayer();
            cfg.teams[0].players[4].name = textCut(textBox1Player5.Text, 32);

            cfg.teams[1] = new JTeam();
            cfg.teams[1].teamName = textCut(textBoxTeam2Name.Text, 16);
            cfg.teams[1].players = new JPlayer[5];
            cfg.teams[1].players[0] = new JPlayer();
            cfg.teams[1].players[0].name = textCut(textBox2Player1.Text, 32);
            cfg.teams[1].players[1] = new JPlayer();
            cfg.teams[1].players[1].name = textCut(textBox2Player2.Text, 32);
            cfg.teams[1].players[2] = new JPlayer();
            cfg.teams[1].players[2].name = textCut(textBox2Player3.Text, 32);
            cfg.teams[1].players[3] = new JPlayer();
            cfg.teams[1].players[3].name = textCut(textBox2Player4.Text, 32);
            cfg.teams[1].players[4] = new JPlayer();
            cfg.teams[1].players[4].name = textCut(textBox2Player5.Text, 32);

            cfg.rounds = new JRound[5];
            for (int i = 0; i < 5; i++)
            {
                cfg.rounds[i] = new JRound();
            }

            cfg.rounds[0].answers = new JAnswer[8];
            for (int i = 0; i < 8; i++)
            {
                cfg.rounds[0].answers[i] = new JAnswer();
            }
            cfg.rounds[0].answers[0].name = textCut(textBoxRound1Answer1.Text, 28);
            cfg.rounds[0].answers[0].cost = textCut(textBoxRound1Answer1Score.Text, 2);
            cfg.rounds[0].answers[1].name = textCut(textBoxRound1Answer2.Text, 28);
            cfg.rounds[0].answers[1].cost = paddedString(textBoxRound1Answer2Score.Text, 2);
            cfg.rounds[0].answers[2].name = textCut(textBoxRound1Answer3.Text, 28);
            cfg.rounds[0].answers[2].cost = paddedString(textBoxRound1Answer3Score.Text, 2);
            cfg.rounds[0].answers[3].name = textCut(textBoxRound1Answer4.Text, 28);
            cfg.rounds[0].answers[3].cost = paddedString(textBoxRound1Answer4Score.Text, 2);
            cfg.rounds[0].answers[4].name = textCut(textBoxRound1Answer5.Text, 28);
            cfg.rounds[0].answers[4].cost = paddedString(textBoxRound1Answer5Score.Text, 2);
            cfg.rounds[0].answers[5].name = textCut(textBoxRound1Answer6.Text, 28);
            cfg.rounds[0].answers[5].cost = paddedString(textBoxRound1Answer6Score.Text, 2);
            cfg.rounds[0].answers[6].name = textCut(textBoxRound1Answer7.Text, 28);
            cfg.rounds[0].answers[6].cost = paddedString(textBoxRound1Answer7Score.Text, 2);
            cfg.rounds[0].answers[7].name = textCut(textBoxRound1Answer8.Text, 28);
            cfg.rounds[0].answers[7].cost = paddedString(textBoxRound1Answer8Score.Text, 2);

            cfg.rounds[1].answers = new JAnswer[8];
            for (int i = 0; i < 8; i++)
            {
                cfg.rounds[1].answers[i] = new JAnswer();
            }
            cfg.rounds[1].answers[0].name = textCut(textBoxRound2Answer1.Text, 28);
            cfg.rounds[1].answers[0].cost = paddedString(textBoxRound2Answer1Score.Text, 2);
            cfg.rounds[1].answers[1].name = textCut(textBoxRound2Answer2.Text, 28);
            cfg.rounds[1].answers[1].cost = paddedString(textBoxRound2Answer2Score.Text, 2);
            cfg.rounds[1].answers[2].name = textCut(textBoxRound2Answer3.Text, 28);
            cfg.rounds[1].answers[2].cost = paddedString(textBoxRound2Answer3Score.Text, 2);
            cfg.rounds[1].answers[3].name = textCut(textBoxRound2Answer4.Text, 28);
            cfg.rounds[1].answers[3].cost = paddedString(textBoxRound2Answer4Score.Text, 2);
            cfg.rounds[1].answers[4].name = textCut(textBoxRound2Answer5.Text, 28);
            cfg.rounds[1].answers[4].cost = paddedString(textBoxRound2Answer5Score.Text, 2);
            cfg.rounds[1].answers[5].name = textCut(textBoxRound2Answer6.Text, 28);
            cfg.rounds[1].answers[5].cost = paddedString(textBoxRound2Answer6Score.Text, 2);
            cfg.rounds[1].answers[6].name = textCut(textBoxRound2Answer7.Text, 28);
            cfg.rounds[1].answers[6].cost = paddedString(textBoxRound2Answer7Score.Text, 2);
            cfg.rounds[1].answers[7].name = textCut(textBoxRound2Answer8.Text, 28);
            cfg.rounds[1].answers[7].cost = paddedString(textBoxRound2Answer8Score.Text, 2);

            cfg.rounds[2].answers = new JAnswer[8];
            for (int i = 0; i < 8; i++)
            {
                cfg.rounds[2].answers[i] = new JAnswer();
            }
            cfg.rounds[2].answers[0].name = textCut(textBoxRound3Answer1.Text, 28);
            cfg.rounds[2].answers[0].cost = paddedString(textBoxRound3Answer1Score.Text, 2);
            cfg.rounds[2].answers[1].name = textCut(textBoxRound3Answer2.Text, 28);
            cfg.rounds[2].answers[1].cost = paddedString(textBoxRound3Answer2Score.Text, 2);
            cfg.rounds[2].answers[2].name = textCut(textBoxRound3Answer3.Text, 28);
            cfg.rounds[2].answers[2].cost = paddedString(textBoxRound3Answer3Score.Text, 2);
            cfg.rounds[2].answers[3].name = textCut(textBoxRound3Answer4.Text, 28);
            cfg.rounds[2].answers[3].cost = paddedString(textBoxRound3Answer4Score.Text, 2);
            cfg.rounds[2].answers[4].name = textCut(textBoxRound3Answer5.Text, 28);
            cfg.rounds[2].answers[4].cost = paddedString(textBoxRound3Answer5Score.Text, 2);
            cfg.rounds[2].answers[5].name = textCut(textBoxRound3Answer6.Text, 28);
            cfg.rounds[2].answers[5].cost = paddedString(textBoxRound3Answer6Score.Text, 2);
            cfg.rounds[2].answers[6].name = textCut(textBoxRound3Answer7.Text, 28);
            cfg.rounds[2].answers[6].cost = paddedString(textBoxRound3Answer7Score.Text, 2);
            cfg.rounds[2].answers[7].name = textCut(textBoxRound3Answer8.Text, 28);
            cfg.rounds[2].answers[7].cost = paddedString(textBoxRound3Answer8Score.Text, 2);

            cfg.rounds[3].answers = new JAnswer[8];
            for (int i = 0; i < 8; i++)
            {
                cfg.rounds[3].answers[i] = new JAnswer();
            }
            cfg.rounds[3].answers[0].name = textCut(textBoxRound4Answer1.Text, 28);
            cfg.rounds[3].answers[0].cost = paddedString(textBoxRound4Answer1Score.Text, 2);
            cfg.rounds[3].answers[1].name = textCut(textBoxRound4Answer2.Text, 28);
            cfg.rounds[3].answers[1].cost = paddedString(textBoxRound4Answer2Score.Text, 2);
            cfg.rounds[3].answers[2].name = textCut(textBoxRound4Answer3.Text, 28);
            cfg.rounds[3].answers[2].cost = paddedString(textBoxRound4Answer3Score.Text, 2);
            cfg.rounds[3].answers[3].name = textCut(textBoxRound4Answer4.Text, 28);
            cfg.rounds[3].answers[3].cost = paddedString(textBoxRound4Answer4Score.Text, 2);
            cfg.rounds[3].answers[4].name = textCut(textBoxRound4Answer5.Text, 28);
            cfg.rounds[3].answers[4].cost = paddedString(textBoxRound4Answer5Score.Text, 2);
            cfg.rounds[3].answers[5].name = textCut(textBoxRound4Answer6.Text, 28);
            cfg.rounds[3].answers[5].cost = paddedString(textBoxRound4Answer6Score.Text, 2);
            cfg.rounds[3].answers[6].name = textCut(textBoxRound4Answer7.Text, 28);
            cfg.rounds[3].answers[6].cost = paddedString(textBoxRound4Answer7Score.Text, 2);
            cfg.rounds[3].answers[7].name = textCut(textBoxRound4Answer8.Text, 28);
            cfg.rounds[3].answers[7].cost = paddedString(textBoxRound4Answer8Score.Text, 2);

            cfg.rounds[4].answers = new JAnswer[8];
            for (int i = 0; i < 8; i++)
            {
                cfg.rounds[4].answers[i] = new JAnswer();
            }
            cfg.rounds[4].answers[0].name = textCut(textBoxRound5Answer1.Text, 28);
            cfg.rounds[4].answers[0].cost = paddedString(textBoxRound5Answer1Score.Text, 2);
            cfg.rounds[4].answers[1].name = textCut(textBoxRound5Answer2.Text, 28);
            cfg.rounds[4].answers[1].cost = paddedString(textBoxRound5Answer2Score.Text, 2);
            cfg.rounds[4].answers[2].name = textCut(textBoxRound5Answer3.Text, 28);
            cfg.rounds[4].answers[2].cost = paddedString(textBoxRound5Answer3Score.Text, 2);
            cfg.rounds[4].answers[3].name = textCut(textBoxRound5Answer4.Text, 28);
            cfg.rounds[4].answers[3].cost = paddedString(textBoxRound5Answer4Score.Text, 2);
            cfg.rounds[4].answers[4].name = textCut(textBoxRound5Answer5.Text, 28);
            cfg.rounds[4].answers[4].cost = paddedString(textBoxRound5Answer5Score.Text, 2);
            cfg.rounds[4].answers[5].name = textCut(textBoxRound5Answer6.Text, 28);
            cfg.rounds[4].answers[5].cost = paddedString(textBoxRound5Answer6Score.Text, 2);
            cfg.rounds[4].answers[6].name = textCut(textBoxRound5Answer7.Text, 28);
            cfg.rounds[4].answers[6].cost = paddedString(textBoxRound5Answer7Score.Text, 2);
            cfg.rounds[4].answers[7].name = textCut(textBoxRound5Answer8.Text, 28);
            cfg.rounds[4].answers[7].cost = paddedString(textBoxRound5Answer8Score.Text, 2);

            cfg.final = new JFinalRound();
            cfg.final.answersInit = new JAnswer[5];
            for (int i = 0; i < 5; i++)
            {
                cfg.final.answersInit[i] = new JAnswer();
            }
            cfg.final.answersInit[0].name = textCut(textBoxFinalPreAnswer1.Text, 28);
            cfg.final.answersInit[0].cost = paddedString(textBoxFinalPreAnswer1Score.Text, 2);
            cfg.final.answersInit[1].name = textCut(textBoxFinalPreAnswer2.Text, 28);
            cfg.final.answersInit[1].cost = paddedString(textBoxFinalPreAnswer2Score.Text, 2);
            cfg.final.answersInit[2].name = textCut(textBoxFinalPreAnswer3.Text, 28);
            cfg.final.answersInit[2].cost = paddedString(textBoxFinalPreAnswer3Score.Text, 2);
            cfg.final.answersInit[3].name = textCut(textBoxFinalPreAnswer4.Text, 28);
            cfg.final.answersInit[3].cost = paddedString(textBoxFinalPreAnswer4Score.Text, 2);
            cfg.final.answersInit[4].name = textCut(textBoxFinalPreAnswer5.Text, 28);
            cfg.final.answersInit[4].cost = paddedString(textBoxFinalPreAnswer5Score.Text, 2);

            cfg.final.answersAfter = new JAnswer[5];
            for (int i = 0; i < 5; i++)
            {
                cfg.final.answersAfter[i] = new JAnswer();
            }
            cfg.final.answersAfter[0].name = textCut(textBoxFinalPostAnswer1.Text, 28);
            cfg.final.answersAfter[0].cost = paddedString(textBoxFinalPostAnswer1Score.Text, 2);
            cfg.final.answersAfter[1].name = textCut(textBoxFinalPostAnswer2.Text, 28);
            cfg.final.answersAfter[1].cost = paddedString(textBoxFinalPostAnswer2Score.Text, 2);
            cfg.final.answersAfter[2].name = textCut(textBoxFinalPostAnswer3.Text, 28);
            cfg.final.answersAfter[2].cost = paddedString(textBoxFinalPostAnswer3Score.Text, 2);
            cfg.final.answersAfter[3].name = textCut(textBoxFinalPostAnswer4.Text, 28);
            cfg.final.answersAfter[3].cost = paddedString(textBoxFinalPostAnswer4Score.Text, 2);
            cfg.final.answersAfter[4].name = textCut(textBoxFinalPostAnswer5.Text, 28);
            cfg.final.answersAfter[4].cost = paddedString(textBoxFinalPostAnswer5Score.Text, 2);

            return cfg;
        }

        private void UpdateUIFromState(Status state, string[] resolvedLines)
        {
            using (MuteScope mute = new MuteScope(this))
            {
                // even more oh no
                textBoxRoundNumber.Text = new string((char)state.curRound[3], 1);
                textBoxState.Text = Convert.ToString(state.internalState);
                textBoxScore.Text = Convert.ToString(state.pendingScore);
                textBoxTeam1Score.Text = Config.BytesToText(state.scores[0].score);
                textBoxTeam2Score.Text = Config.BytesToText(state.scores[1].score);
                richTextBoxControls.Lines = resolvedLines;
            }
        }

        private void UpdateUIFromConfig(Config cfg)
        {
            using (MuteScope mute = new MuteScope(this))
            {
                // even more oh no
                textBoxRoundNumber.Text = new string((char)cfg.state.curRound[3], 1);
                textBoxState.Text = Convert.ToString(cfg.state.internalState);
                textBoxScore.Text = Convert.ToString(cfg.state.pendingScore);
                textBoxTeam1Score.Text = Config.BytesToText(cfg.state.scores[0].score);
                textBoxTeam2Score.Text = Config.BytesToText(cfg.state.scores[1].score);

                textBoxTeam1Name.Text = Config.BytesToText(cfg.teams[0].teamName);
                textBox1Player1.Text = Config.BytesToText(cfg.teams[0].players[0].name);
                textBox1Player2.Text = Config.BytesToText(cfg.teams[0].players[1].name);
                textBox1Player3.Text = Config.BytesToText(cfg.teams[0].players[2].name);
                textBox1Player4.Text = Config.BytesToText(cfg.teams[0].players[3].name);
                textBox1Player5.Text = Config.BytesToText(cfg.teams[0].players[4].name);

                textBoxTeam2Name.Text = Config.BytesToText(cfg.teams[1].teamName);
                textBox2Player1.Text = Config.BytesToText(cfg.teams[1].players[0].name);
                textBox2Player2.Text = Config.BytesToText(cfg.teams[1].players[1].name);
                textBox2Player3.Text = Config.BytesToText(cfg.teams[1].players[2].name);
                textBox2Player4.Text = Config.BytesToText(cfg.teams[1].players[3].name);
                textBox2Player5.Text = Config.BytesToText(cfg.teams[1].players[4].name);

                textBoxRound1Answer1.Text      = Config.BytesToText(cfg.rounds[0].answers[0].name);
                textBoxRound1Answer1Score.Text = Config.BytesToText(cfg.rounds[0].answers[0].cost);
                textBoxRound1Answer2.Text      = Config.BytesToText(cfg.rounds[0].answers[1].name);
                textBoxRound1Answer2Score.Text = Config.BytesToText(cfg.rounds[0].answers[1].cost);
                textBoxRound1Answer3.Text      = Config.BytesToText(cfg.rounds[0].answers[2].name);
                textBoxRound1Answer3Score.Text = Config.BytesToText(cfg.rounds[0].answers[2].cost);
                textBoxRound1Answer4.Text      = Config.BytesToText(cfg.rounds[0].answers[3].name);
                textBoxRound1Answer4Score.Text = Config.BytesToText(cfg.rounds[0].answers[3].cost);
                textBoxRound1Answer5.Text      = Config.BytesToText(cfg.rounds[0].answers[4].name);
                textBoxRound1Answer5Score.Text = Config.BytesToText(cfg.rounds[0].answers[4].cost);
                textBoxRound1Answer6.Text      = Config.BytesToText(cfg.rounds[0].answers[5].name);
                textBoxRound1Answer6Score.Text = Config.BytesToText(cfg.rounds[0].answers[5].cost);
                textBoxRound1Answer7.Text      = Config.BytesToText(cfg.rounds[0].answers[6].name);
                textBoxRound1Answer7Score.Text = Config.BytesToText(cfg.rounds[0].answers[6].cost);
                textBoxRound1Answer8.Text      = Config.BytesToText(cfg.rounds[0].answers[7].name);
                textBoxRound1Answer8Score.Text = Config.BytesToText(cfg.rounds[0].answers[7].cost);

                textBoxRound2Answer1.Text      = Config.BytesToText(cfg.rounds[1].answers[0].name);
                textBoxRound2Answer1Score.Text = Config.BytesToText(cfg.rounds[1].answers[0].cost);
                textBoxRound2Answer2.Text      = Config.BytesToText(cfg.rounds[1].answers[1].name);
                textBoxRound2Answer2Score.Text = Config.BytesToText(cfg.rounds[1].answers[1].cost);
                textBoxRound2Answer3.Text      = Config.BytesToText(cfg.rounds[1].answers[2].name);
                textBoxRound2Answer3Score.Text = Config.BytesToText(cfg.rounds[1].answers[2].cost);
                textBoxRound2Answer4.Text      = Config.BytesToText(cfg.rounds[1].answers[3].name);
                textBoxRound2Answer4Score.Text = Config.BytesToText(cfg.rounds[1].answers[3].cost);
                textBoxRound2Answer5.Text      = Config.BytesToText(cfg.rounds[1].answers[4].name);
                textBoxRound2Answer5Score.Text = Config.BytesToText(cfg.rounds[1].answers[4].cost);
                textBoxRound2Answer6.Text      = Config.BytesToText(cfg.rounds[1].answers[5].name);
                textBoxRound2Answer6Score.Text = Config.BytesToText(cfg.rounds[1].answers[5].cost);
                textBoxRound2Answer7.Text      = Config.BytesToText(cfg.rounds[1].answers[6].name);
                textBoxRound2Answer7Score.Text = Config.BytesToText(cfg.rounds[1].answers[6].cost);
                textBoxRound2Answer8.Text      = Config.BytesToText(cfg.rounds[1].answers[7].name);
                textBoxRound2Answer8Score.Text = Config.BytesToText(cfg.rounds[1].answers[7].cost);

                textBoxRound3Answer1.Text      = Config.BytesToText(cfg.rounds[2].answers[0].name);
                textBoxRound3Answer1Score.Text = Config.BytesToText(cfg.rounds[2].answers[0].cost);
                textBoxRound3Answer2.Text      = Config.BytesToText(cfg.rounds[2].answers[1].name);
                textBoxRound3Answer2Score.Text = Config.BytesToText(cfg.rounds[2].answers[1].cost);
                textBoxRound3Answer3.Text      = Config.BytesToText(cfg.rounds[2].answers[2].name);
                textBoxRound3Answer3Score.Text = Config.BytesToText(cfg.rounds[2].answers[2].cost);
                textBoxRound3Answer4.Text      = Config.BytesToText(cfg.rounds[2].answers[3].name);
                textBoxRound3Answer4Score.Text = Config.BytesToText(cfg.rounds[2].answers[3].cost);
                textBoxRound3Answer5.Text      = Config.BytesToText(cfg.rounds[2].answers[4].name);
                textBoxRound3Answer5Score.Text = Config.BytesToText(cfg.rounds[2].answers[4].cost);
                textBoxRound3Answer6.Text      = Config.BytesToText(cfg.rounds[2].answers[5].name);
                textBoxRound3Answer6Score.Text = Config.BytesToText(cfg.rounds[2].answers[5].cost);
                textBoxRound3Answer7.Text      = Config.BytesToText(cfg.rounds[2].answers[6].name);
                textBoxRound3Answer7Score.Text = Config.BytesToText(cfg.rounds[2].answers[6].cost);
                textBoxRound3Answer8.Text      = Config.BytesToText(cfg.rounds[2].answers[7].name);
                textBoxRound3Answer8Score.Text = Config.BytesToText(cfg.rounds[2].answers[7].cost);

                textBoxRound4Answer1.Text      = Config.BytesToText(cfg.rounds[3].answers[0].name);
                textBoxRound4Answer1Score.Text = Config.BytesToText(cfg.rounds[3].answers[0].cost);
                textBoxRound4Answer2.Text      = Config.BytesToText(cfg.rounds[3].answers[1].name);
                textBoxRound4Answer2Score.Text = Config.BytesToText(cfg.rounds[3].answers[1].cost);
                textBoxRound4Answer3.Text      = Config.BytesToText(cfg.rounds[3].answers[2].name);
                textBoxRound4Answer3Score.Text = Config.BytesToText(cfg.rounds[3].answers[2].cost);
                textBoxRound4Answer4.Text      = Config.BytesToText(cfg.rounds[3].answers[3].name);
                textBoxRound4Answer4Score.Text = Config.BytesToText(cfg.rounds[3].answers[3].cost);
                textBoxRound4Answer5.Text      = Config.BytesToText(cfg.rounds[3].answers[4].name);
                textBoxRound4Answer5Score.Text = Config.BytesToText(cfg.rounds[3].answers[4].cost);
                textBoxRound4Answer6.Text      = Config.BytesToText(cfg.rounds[3].answers[5].name);
                textBoxRound4Answer6Score.Text = Config.BytesToText(cfg.rounds[3].answers[5].cost);
                textBoxRound4Answer7.Text      = Config.BytesToText(cfg.rounds[3].answers[6].name);
                textBoxRound4Answer7Score.Text = Config.BytesToText(cfg.rounds[3].answers[6].cost);
                textBoxRound4Answer8.Text      = Config.BytesToText(cfg.rounds[3].answers[7].name);
                textBoxRound4Answer8Score.Text = Config.BytesToText(cfg.rounds[3].answers[7].cost);

                textBoxRound5Answer1.Text      = Config.BytesToText(cfg.rounds[4].answers[0].name);
                textBoxRound5Answer1Score.Text = Config.BytesToText(cfg.rounds[4].answers[0].cost);
                textBoxRound5Answer2.Text      = Config.BytesToText(cfg.rounds[4].answers[1].name);
                textBoxRound5Answer2Score.Text = Config.BytesToText(cfg.rounds[4].answers[1].cost);
                textBoxRound5Answer3.Text      = Config.BytesToText(cfg.rounds[4].answers[2].name);
                textBoxRound5Answer3Score.Text = Config.BytesToText(cfg.rounds[4].answers[2].cost);
                textBoxRound5Answer4.Text      = Config.BytesToText(cfg.rounds[4].answers[3].name);
                textBoxRound5Answer4Score.Text = Config.BytesToText(cfg.rounds[4].answers[3].cost);
                textBoxRound5Answer5.Text      = Config.BytesToText(cfg.rounds[4].answers[4].name);
                textBoxRound5Answer5Score.Text = Config.BytesToText(cfg.rounds[4].answers[4].cost);
                textBoxRound5Answer6.Text      = Config.BytesToText(cfg.rounds[4].answers[5].name);
                textBoxRound5Answer6Score.Text = Config.BytesToText(cfg.rounds[4].answers[5].cost);
                textBoxRound5Answer7.Text      = Config.BytesToText(cfg.rounds[4].answers[6].name);
                textBoxRound5Answer7Score.Text = Config.BytesToText(cfg.rounds[4].answers[6].cost);
                textBoxRound5Answer8.Text      = Config.BytesToText(cfg.rounds[4].answers[7].name);
                textBoxRound5Answer8Score.Text = Config.BytesToText(cfg.rounds[4].answers[7].cost);

                textBoxFinalPreAnswer1.Text      = Config.BytesToText(cfg.final.answersInit[0].name);
                textBoxFinalPreAnswer1Score.Text = Config.BytesToText(cfg.final.answersInit[0].cost);
                textBoxFinalPreAnswer2.Text      = Config.BytesToText(cfg.final.answersInit[1].name);
                textBoxFinalPreAnswer2Score.Text = Config.BytesToText(cfg.final.answersInit[1].cost);
                textBoxFinalPreAnswer3.Text      = Config.BytesToText(cfg.final.answersInit[2].name);
                textBoxFinalPreAnswer3Score.Text = Config.BytesToText(cfg.final.answersInit[2].cost);
                textBoxFinalPreAnswer4.Text      = Config.BytesToText(cfg.final.answersInit[3].name);
                textBoxFinalPreAnswer4Score.Text = Config.BytesToText(cfg.final.answersInit[3].cost);
                textBoxFinalPreAnswer5.Text      = Config.BytesToText(cfg.final.answersInit[4].name);
                textBoxFinalPreAnswer5Score.Text = Config.BytesToText(cfg.final.answersInit[4].cost);

                textBoxFinalPostAnswer1.Text      = Config.BytesToText(cfg.final.answersAfter[0].name);
                textBoxFinalPostAnswer1Score.Text = Config.BytesToText(cfg.final.answersAfter[0].cost);
                textBoxFinalPostAnswer2.Text      = Config.BytesToText(cfg.final.answersAfter[1].name);
                textBoxFinalPostAnswer2Score.Text = Config.BytesToText(cfg.final.answersAfter[1].cost);
                textBoxFinalPostAnswer3.Text      = Config.BytesToText(cfg.final.answersAfter[2].name);
                textBoxFinalPostAnswer3Score.Text = Config.BytesToText(cfg.final.answersAfter[2].cost);
                textBoxFinalPostAnswer4.Text      = Config.BytesToText(cfg.final.answersAfter[3].name);
                textBoxFinalPostAnswer4Score.Text = Config.BytesToText(cfg.final.answersAfter[3].cost);
                textBoxFinalPostAnswer5.Text      = Config.BytesToText(cfg.final.answersAfter[4].name);
                textBoxFinalPostAnswer5Score.Text = Config.BytesToText(cfg.final.answersAfter[4].cost);
            }
        }

        private void UpdateUIFromConfig(JConfig cfg)
        {
            using (MuteScope mute = new MuteScope(this))
            {
                // even more oh no
                textBoxTeam1Score.Text = (cfg.state.scores[0].score);
                textBoxTeam2Score.Text = (cfg.state.scores[1].score);

                textBoxTeam1Name.Text = (cfg.teams[0].teamName);
                textBox1Player1.Text = (cfg.teams[0].players[0].name);
                textBox1Player2.Text = (cfg.teams[0].players[1].name);
                textBox1Player3.Text = (cfg.teams[0].players[2].name);
                textBox1Player4.Text = (cfg.teams[0].players[3].name);
                textBox1Player5.Text = (cfg.teams[0].players[4].name);

                textBoxTeam2Name.Text = (cfg.teams[1].teamName);
                textBox2Player1.Text = (cfg.teams[1].players[0].name);
                textBox2Player2.Text = (cfg.teams[1].players[1].name);
                textBox2Player3.Text = (cfg.teams[1].players[2].name);
                textBox2Player4.Text = (cfg.teams[1].players[3].name);
                textBox2Player5.Text = (cfg.teams[1].players[4].name);

                textBoxRound1Answer1.Text = (cfg.rounds[0].answers[0].name);
                textBoxRound1Answer1Score.Text = (cfg.rounds[0].answers[0].cost);
                textBoxRound1Answer2.Text = (cfg.rounds[0].answers[1].name);
                textBoxRound1Answer2Score.Text = (cfg.rounds[0].answers[1].cost);
                textBoxRound1Answer3.Text = (cfg.rounds[0].answers[2].name);
                textBoxRound1Answer3Score.Text = (cfg.rounds[0].answers[2].cost);
                textBoxRound1Answer4.Text = (cfg.rounds[0].answers[3].name);
                textBoxRound1Answer4Score.Text = (cfg.rounds[0].answers[3].cost);
                textBoxRound1Answer5.Text = (cfg.rounds[0].answers[4].name);
                textBoxRound1Answer5Score.Text = (cfg.rounds[0].answers[4].cost);
                textBoxRound1Answer6.Text = (cfg.rounds[0].answers[5].name);
                textBoxRound1Answer6Score.Text = (cfg.rounds[0].answers[5].cost);
                textBoxRound1Answer7.Text = (cfg.rounds[0].answers[6].name);
                textBoxRound1Answer7Score.Text = (cfg.rounds[0].answers[6].cost);
                textBoxRound1Answer8.Text = (cfg.rounds[0].answers[7].name);
                textBoxRound1Answer8Score.Text = (cfg.rounds[0].answers[7].cost);

                textBoxRound2Answer1.Text = (cfg.rounds[1].answers[0].name);
                textBoxRound2Answer1Score.Text = (cfg.rounds[1].answers[0].cost);
                textBoxRound2Answer2.Text = (cfg.rounds[1].answers[1].name);
                textBoxRound2Answer2Score.Text = (cfg.rounds[1].answers[1].cost);
                textBoxRound2Answer3.Text = (cfg.rounds[1].answers[2].name);
                textBoxRound2Answer3Score.Text = (cfg.rounds[1].answers[2].cost);
                textBoxRound2Answer4.Text = (cfg.rounds[1].answers[3].name);
                textBoxRound2Answer4Score.Text = (cfg.rounds[1].answers[3].cost);
                textBoxRound2Answer5.Text = (cfg.rounds[1].answers[4].name);
                textBoxRound2Answer5Score.Text = (cfg.rounds[1].answers[4].cost);
                textBoxRound2Answer6.Text = (cfg.rounds[1].answers[5].name);
                textBoxRound2Answer6Score.Text = (cfg.rounds[1].answers[5].cost);
                textBoxRound2Answer7.Text = (cfg.rounds[1].answers[6].name);
                textBoxRound2Answer7Score.Text = (cfg.rounds[1].answers[6].cost);
                textBoxRound2Answer8.Text = (cfg.rounds[1].answers[7].name);
                textBoxRound2Answer8Score.Text = (cfg.rounds[1].answers[7].cost);

                textBoxRound3Answer1.Text = (cfg.rounds[2].answers[0].name);
                textBoxRound3Answer1Score.Text = (cfg.rounds[2].answers[0].cost);
                textBoxRound3Answer2.Text = (cfg.rounds[2].answers[1].name);
                textBoxRound3Answer2Score.Text = (cfg.rounds[2].answers[1].cost);
                textBoxRound3Answer3.Text = (cfg.rounds[2].answers[2].name);
                textBoxRound3Answer3Score.Text = (cfg.rounds[2].answers[2].cost);
                textBoxRound3Answer4.Text = (cfg.rounds[2].answers[3].name);
                textBoxRound3Answer4Score.Text = (cfg.rounds[2].answers[3].cost);
                textBoxRound3Answer5.Text = (cfg.rounds[2].answers[4].name);
                textBoxRound3Answer5Score.Text = (cfg.rounds[2].answers[4].cost);
                textBoxRound3Answer6.Text = (cfg.rounds[2].answers[5].name);
                textBoxRound3Answer6Score.Text = (cfg.rounds[2].answers[5].cost);
                textBoxRound3Answer7.Text = (cfg.rounds[2].answers[6].name);
                textBoxRound3Answer7Score.Text = (cfg.rounds[2].answers[6].cost);
                textBoxRound3Answer8.Text = (cfg.rounds[2].answers[7].name);
                textBoxRound3Answer8Score.Text = (cfg.rounds[2].answers[7].cost);

                textBoxRound4Answer1.Text = (cfg.rounds[3].answers[0].name);
                textBoxRound4Answer1Score.Text = (cfg.rounds[3].answers[0].cost);
                textBoxRound4Answer2.Text = (cfg.rounds[3].answers[1].name);
                textBoxRound4Answer2Score.Text = (cfg.rounds[3].answers[1].cost);
                textBoxRound4Answer3.Text = (cfg.rounds[3].answers[2].name);
                textBoxRound4Answer3Score.Text = (cfg.rounds[3].answers[2].cost);
                textBoxRound4Answer4.Text = (cfg.rounds[3].answers[3].name);
                textBoxRound4Answer4Score.Text = (cfg.rounds[3].answers[3].cost);
                textBoxRound4Answer5.Text = (cfg.rounds[3].answers[4].name);
                textBoxRound4Answer5Score.Text = (cfg.rounds[3].answers[4].cost);
                textBoxRound4Answer6.Text = (cfg.rounds[3].answers[5].name);
                textBoxRound4Answer6Score.Text = (cfg.rounds[3].answers[5].cost);
                textBoxRound4Answer7.Text = (cfg.rounds[3].answers[6].name);
                textBoxRound4Answer7Score.Text = (cfg.rounds[3].answers[6].cost);
                textBoxRound4Answer8.Text = (cfg.rounds[3].answers[7].name);
                textBoxRound4Answer8Score.Text = (cfg.rounds[3].answers[7].cost);

                textBoxRound5Answer1.Text = (cfg.rounds[4].answers[0].name);
                textBoxRound5Answer1Score.Text = (cfg.rounds[4].answers[0].cost);
                textBoxRound5Answer2.Text = (cfg.rounds[4].answers[1].name);
                textBoxRound5Answer2Score.Text = (cfg.rounds[4].answers[1].cost);
                textBoxRound5Answer3.Text = (cfg.rounds[4].answers[2].name);
                textBoxRound5Answer3Score.Text = (cfg.rounds[4].answers[2].cost);
                textBoxRound5Answer4.Text = (cfg.rounds[4].answers[3].name);
                textBoxRound5Answer4Score.Text = (cfg.rounds[4].answers[3].cost);
                textBoxRound5Answer5.Text = (cfg.rounds[4].answers[4].name);
                textBoxRound5Answer5Score.Text = (cfg.rounds[4].answers[4].cost);
                textBoxRound5Answer6.Text = (cfg.rounds[4].answers[5].name);
                textBoxRound5Answer6Score.Text = (cfg.rounds[4].answers[5].cost);
                textBoxRound5Answer7.Text = (cfg.rounds[4].answers[6].name);
                textBoxRound5Answer7Score.Text = (cfg.rounds[4].answers[6].cost);
                textBoxRound5Answer8.Text = (cfg.rounds[4].answers[7].name);
                textBoxRound5Answer8Score.Text = (cfg.rounds[4].answers[7].cost);

                textBoxFinalPreAnswer1.Text = (cfg.final.answersInit[0].name);
                textBoxFinalPreAnswer1Score.Text = (cfg.final.answersInit[0].cost);
                textBoxFinalPreAnswer2.Text = (cfg.final.answersInit[1].name);
                textBoxFinalPreAnswer2Score.Text = (cfg.final.answersInit[1].cost);
                textBoxFinalPreAnswer3.Text = (cfg.final.answersInit[2].name);
                textBoxFinalPreAnswer3Score.Text = (cfg.final.answersInit[2].cost);
                textBoxFinalPreAnswer4.Text = (cfg.final.answersInit[3].name);
                textBoxFinalPreAnswer4Score.Text = (cfg.final.answersInit[3].cost);
                textBoxFinalPreAnswer5.Text = (cfg.final.answersInit[4].name);
                textBoxFinalPreAnswer5Score.Text = (cfg.final.answersInit[4].cost);

                textBoxFinalPostAnswer1.Text = (cfg.final.answersAfter[0].name);
                textBoxFinalPostAnswer1Score.Text = (cfg.final.answersAfter[0].cost);
                textBoxFinalPostAnswer2.Text = (cfg.final.answersAfter[1].name);
                textBoxFinalPostAnswer2Score.Text = (cfg.final.answersAfter[1].cost);
                textBoxFinalPostAnswer3.Text = (cfg.final.answersAfter[2].name);
                textBoxFinalPostAnswer3Score.Text = (cfg.final.answersAfter[2].cost);
                textBoxFinalPostAnswer4.Text = (cfg.final.answersAfter[3].name);
                textBoxFinalPostAnswer4Score.Text = (cfg.final.answersAfter[3].cost);
                textBoxFinalPostAnswer5.Text = (cfg.final.answersAfter[4].name);
                textBoxFinalPostAnswer5Score.Text = (cfg.final.answersAfter[4].cost);
            }
        }

        private void UpdateConfig(JConfig jconfig)
        {
            UpdateUIFromConfig(jconfig);
            var config = MakeConfig();
            _wantToUpdateConfig = config;
            _config = config;
            if (_timer is object)
                _timer.Change(0 /*now*/, Timeout.Infinite);
        }

        private void buttonSaveConfig_Click(object sender, EventArgs e)
        {
            SaveFileDialog sfd = new SaveFileDialog
            {
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                FilterIndex = 1,
                RestoreDirectory = true,
                FileName = DEFAULT_CONFIG_NAME
            };

            var jcfg = MakeJConfig();
            var jsStr = JsonSerializer.Serialize(jcfg);

            if (sfd.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    using (var writer = new FileStream(sfd.FileName, FileMode.Create))
                    {
                        JsonSerializer.Serialize(writer, MakeJConfig());
                    }

                    MessageBox.Show("Config was saved successfully!", "hacktice", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch(Exception)
                {
                    MessageBox.Show("Failed to save config file!", "hacktice", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void buttonLoadConfig_Click(object sender, EventArgs e)
        {
            OpenFileDialog ofd = new OpenFileDialog
            {
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                FilterIndex = 1,
                RestoreDirectory = true,
            };

            if (ofd.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    using (var reader = ofd.OpenFile())
                    {
                        UpdateConfig(JsonSerializer.Deserialize<JConfig>(reader));
                    }

                    MessageBox.Show("Config was loaded successfully!", "hacktice", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception)
                {
                    MessageBox.Show("Failed to load config file!", "hacktice", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void Config_CheckedChanged(object sender, EventArgs e)
        {
            if (_muteConfigEvents)
                return;

            try
            {
                var config = MakeConfig();
                _wantToUpdateConfig = config;
                _config = config;
                if (_timer is object)
                    _timer.Change(0 /*now*/, Timeout.Infinite);
            }
            catch(Exception)
            {
            }
        }

        private void buttonSetDefault_Click(object sender, EventArgs e)
        {
            try
            {
                var path = Path.Combine(Application.LocalUserAppDataPath, DEFAULT_CONFIG_NAME);
                using (var writer = new FileStream(path, FileMode.Create))
                {
                    JsonSerializer.Serialize(writer, MakeJConfig());
                }

                MessageBox.Show("Config was saved successfully!", "hacktice", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception)
            {
                MessageBox.Show("Failed to save config file!", "hacktice", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private JConfig GetDefaultConfig()
        {
            // oh no
            var cfg = new JConfig();
            for (int i = 0; i < 2; i++)
                cfg.state.scores[i].score = "000";

            for (int teamId = 0; teamId < 2; teamId++)
            {
                cfg.teams[0].teamName = $"Team {teamId + 1}";
                for (int playerId = 0; playerId < 5; playerId++)
                    cfg.teams[0].players[playerId].name = $"Team {teamId + 1} player {playerId + 1}";
            }

            for (int roundId = 0; roundId < 5; roundId++)
            {
                for (int answerId = 0; answerId < 8; answerId++)
                {
                    cfg.rounds[roundId].answers[answerId].name = $"Round {roundId} answer {answerId}";
                    cfg.rounds[roundId].answers[answerId].cost = $"{answerId * 7}";
                }
            }

            for (int answerId = 0; answerId < 5; answerId++)
            {
                cfg.final.answersInit[answerId].name = $"Final init {answerId}";
                cfg.final.answersInit[answerId].cost = $"{answerId * 7}";
                cfg.final.answersAfter[answerId].name = $"Final after {answerId}";
                cfg.final.answersAfter[answerId].cost = $"{answerId * 3}";
            }

            return cfg;
        }

        private void buttonReset_Click(object sender, EventArgs e)
        {
            UpdateConfig(GetDefaultConfig());
        }

        private void buttonConnToEmu_Click(object sender, EventArgs e)
        {
            if (_timer is null)
            {
                _timer = new System.Threading.Timer(EmulatorStateUpdate, null, 1, Timeout.Infinite);
            }
            else
            {
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                WaitHandle handle = new AutoResetEvent(false);
                _timer.Dispose(handle);
                handle.WaitOne();
                _timer.Dispose();
                _timer = null;
                _stateValue = State.INVALIDATED;

                pictureBoxState.BackColor = Color.DarkGray;
                labelEmulatorState.Text = "Disconnected";
            }
        }

        private void checkBoxCanWriteToEmu_CheckedChanged(object sender, EventArgs e)
        {
            _canWriteToEmu = checkBoxCanWriteToEmu.Checked;
        }
    }
}
