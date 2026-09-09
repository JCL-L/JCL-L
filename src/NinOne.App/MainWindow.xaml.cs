using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using NinOne.App.Runtime;
using NinOne.App.ViewModels;
using NinOne.Application.Devices;
using NinOne.Domain.Configuration;
using NinOne.Domain.Devices;

namespace NinOne.App
{
    public partial class MainWindow : Window
    {
        private readonly DeviceRuntime _runtime;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly ObservableCollection<RelayRow> _relayRows;
        private readonly ObservableCollection<string> _logs = new ObservableCollection<string>();
        private readonly ObservableCollection<string> _gpdSwitchRecords = new ObservableCollection<string>();
        private readonly List<CanFrameRecord> _canFrameRecords = new List<CanFrameRecord>();
        private CancellationTokenSource _gpdCycleCts;
        private Task _gpdCycleTask = Task.CompletedTask;
        private CancellationTokenSource _gpdVoltageRampCts;
        private Task _gpdVoltageRampTask = Task.CompletedTask;
        private CancellationTokenSource _powerContinuousCts;
        private CancellationTokenSource _canReceiveCts;
        private Task _canReceiveTask = Task.CompletedTask;
        private long _canReceivedFrameCount;
        private CancellationTokenSource _canPeriodicSendCts;
        private Task _canPeriodicSendTask = Task.CompletedTask;
        private bool _allowClose;
        private bool _shutdownInProgress;

        public MainWindow(DeviceRuntime runtime)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            InitializeComponent();
            _relayRows = new ObservableCollection<RelayRow>(RelayDefinition.FirstStage.Select(definition => new RelayRow(definition)));
            RelayItems.ItemsSource = _relayRows;
            LogList.ItemsSource = _logs;
            GpdSwitchRecordList.ItemsSource = _gpdSwitchRecords;
            LoadModeBox.ItemsSource = Enum.GetValues(typeof(ElectronicLoadMode));
            LoadModeBox.SelectedItem = ElectronicLoadMode.ConstantCurrent;
            LoadVoltageRateBox.ItemsSource = Enum.GetValues(typeof(ElectronicLoadVoltageRate));
            LoadVoltageRateBox.SelectedItem = ElectronicLoadVoltageRate.Fast;
            DmmFunctionBox.ItemsSource = Enum.GetValues(typeof(MultimeterFunction));
            DmmFunctionBox.SelectedItem = MultimeterFunction.DcVoltage;
            ConfigSourceText.Text = "IP、端口、串口、CAN 和低压负载量程来自当前界面 · INI 仅保留产品及高级参数";
            LoadConfigText.Text = "连接与量程来自当前界面 · 连接时应用";
            ScopeConfigText.Text = "连接地址来自当前界面 · 连接时应用";
            LogPathText.Text = "日志目录：" + Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs") + "（本地文本，无数据库）";
            _runtime.Logger.EntryLogged += Logger_EntryLogged;
            SubscribeRaw(_runtime.Plc, PlcRawText);
            SubscribeRaw(_runtime.AuxiliaryPower, GpdRawText);
            SubscribeRaw(_runtime.ElectronicLoad, LoadRawText);
            SubscribeRaw(_runtime.Multimeter, DmmRawText);
            SubscribeRaw(_runtime.PowerMeter, PowerRawText);
            SubscribeRaw(_runtime.DcdcCan, CanRawText);
            SubscribeRaw(_runtime.Oscilloscope, ScopeRawText);
            SubscribeRaw(_runtime.HighVoltage, HvRawText);
            InitializeConnectionEditors();
            RefreshLoadModeUi();
            RefreshDmmRanges();
            RefreshOpenCanChannels();
            InitializeScopeUi();
        }

        private void SubscribeRaw(IDeviceService service, TextBox textBox)
        {
            service.Trace += (sender, args) => Dispatcher.BeginInvoke(new Action(() =>
            {
                textBox.AppendText(string.Format(CultureInfo.InvariantCulture, "{0:HH:mm:ss.fff} {1,-6} {2}{3}", args.Timestamp, args.Direction, args.Text, Environment.NewLine));
                if (textBox.Text.Length > 200000) textBox.Text = textBox.Text.Substring(textBox.Text.Length - 150000);
                textBox.ScrollToEnd();
            }));
        }

        private void Logger_EntryLogged(object sender, OperationLogEntry entry)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _logs.Add(entry.ToString());
                while (_logs.Count > 2000) _logs.RemoveAt(0);
                if (_logs.Count > 0) LogList.ScrollIntoView(_logs[_logs.Count - 1]);
            }));
        }

        private async Task ExecuteUiAsync(string device, string function, TextBlock resultText, TextBlock errorText, Func<CancellationToken, Task<string>> operation)
        {
            GlobalStatusText.Text = device + " · " + function + " 执行中…";
            errorText.Text = "最近错误：--";
            try
            {
                var result = await operation(_lifetime.Token);
                resultText.Text = "最近结果：" + result;
                GlobalStatusText.Text = device + " · " + function + " 完成";
                await _runtime.Logger.LogAsync(new OperationLogEntry { Level = "INFO", Device = device, Function = function, Message = result });
            }
            catch (OperationCanceledException)
            {
                errorText.Text = "最近错误：操作已取消";
                GlobalStatusText.Text = device + " · " + function + " 已取消";
            }
            catch (Exception exception)
            {
                errorText.Text = "最近错误：" + exception.Message;
                GlobalStatusText.Text = device + " · " + function + " 失败";
                await _runtime.Logger.LogAsync(new OperationLogEntry
                {
                    Level = "ERROR",
                    Device = device,
                    Function = function,
                    ErrorCode = "0x" + exception.HResult.ToString("X8", CultureInfo.InvariantCulture),
                    Message = exception.Message,
                    RawResponse = exception.ToString()
                });
            }
        }

        private Task ConnectAsync(string name, IDeviceService service, TextBlock status, TextBlock result, TextBlock error)
        {
            return ExecuteUiAsync(name, "连接", result, error, async token =>
            {
                await service.ConnectAsync(token);
                status.Text = "已连接";
                return "连接成功";
            });
        }

        private Task DisconnectAsync(string name, IDeviceService service, TextBlock status, TextBlock result, TextBlock error)
        {
            return ExecuteUiAsync(name, "断开", result, error, async token =>
            {
                await service.DisconnectAsync(token);
                status.Text = "未连接";
                return "已断开并释放资源";
            });
        }

        private async void PlcConnect_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteUiAsync("PLC", "连接", PlcResultText, PlcErrorText, async token =>
            {
                if (!_runtime.Plc.IsConnected)
                {
                    ApplyPlcConnectionConfigFromUi();
                    await _runtime.Plc.ConnectAsync(token);
                }
                PlcStatusText.Text = "已连接";
                return string.Format(CultureInfo.InvariantCulture, "连接成功：{0}:{1}", _runtime.Config.Plc.PlcAddress, _runtime.Config.Plc.Port);
            });
        }

        private async void PlcDisconnect_Click(object sender, RoutedEventArgs e)
        {
            await DisconnectAsync("PLC", _runtime.Plc, PlcStatusText, PlcResultText, PlcErrorText);
        }

        private async void PlcRefresh_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteUiAsync("PLC", "读取 K1～K16", PlcResultText, PlcErrorText, async token =>
            {
                var values = await _runtime.Plc.ReadAllAsync(token);
                foreach (var row in _relayRows) row.State = values[row.Id];
                return "已读取 16 个 PLC 输出点的实际状态";
            });
        }

        private async void PlcAllOff_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteUiAsync("PLC", "全部关闭", PlcResultText, PlcErrorText, async token =>
            {
                await _runtime.Plc.AllOffAsync(token);
                foreach (var row in _relayRows) row.State = false;
                return "已按 K16→K1 逐点关闭并回读";
            });
        }

        private async void RelayOn_Click(object sender, RoutedEventArgs e)
        {
            var row = (RelayRow)((Button)sender).Tag;
            if (row.RequiresConfirmation)
            {
                var answer = MessageBox.Show(row.Id + "（" + row.Name + "）属于动力回路。确认写入 ON？", "动力回路二次确认", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes) return;
            }
            await WriteRelayAsync(row, true);
        }

        private async void RelayOff_Click(object sender, RoutedEventArgs e)
        {
            await WriteRelayAsync((RelayRow)((Button)sender).Tag, false);
        }

        private Task WriteRelayAsync(RelayRow row, bool state)
        {
            return ExecuteUiAsync("PLC", row.Id + " " + (state ? "ON" : "OFF"), PlcResultText, PlcErrorText, async token =>
            {
                await _runtime.Plc.WriteRelayAsync(row.Id, state, token);
                row.State = await _runtime.Plc.ReadRelayAsync(row.Id, token);
                return row.Id + " 实际状态=" + (row.State ? "ON" : "OFF");
            });
        }

        private async void GpdConnect_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteUiAsync("GPD2303S", "连接", GpdResultText, GpdErrorText, async token =>
            {
                if (!_runtime.AuxiliaryPower.IsConnected)
                {
                    ApplySerialConfigFromUi(_runtime.Config.AuxiliaryPower, GpdPortText, GpdBaudBox, "GPD2303S");
                    await _runtime.AuxiliaryPower.ConnectAsync(token);
                }
                GpdStatusText.Text = "已连接";
                return string.Format(CultureInfo.InvariantCulture, "连接成功：{0} / {1} / 8N1 / CRLF", _runtime.Config.AuxiliaryPower.PortName, _runtime.Config.AuxiliaryPower.BaudRate);
            });
        }
        private async void GpdDisconnect_Click(object sender, RoutedEventArgs e)
        {
            await StopGpdCycleAsync();
            await StopGpdVoltageRampAsync();
            await DisconnectAsync("GPD2303S", _runtime.AuxiliaryPower, GpdStatusText, GpdResultText, GpdErrorText);
        }

        private async void GpdSet_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteUiAsync("GPD2303S", "设置电压和电流上限", GpdResultText, GpdErrorText, async token =>
            {
                var channel = SelectedNumber(GpdChannelBox);
                var voltage = ParseDecimal(GpdVoltageText.Text, "电压");
                var current = ParseDecimal(GpdCurrentText.Text, "电流上限");
                await _runtime.AuxiliaryPower.SetAsync(channel, voltage, current, token);
                return "CH" + channel + " 已写入并回读：" + voltage + " V / ISET " + current + " A";
            });
        }

        private async void GpdOn_Click(object sender, RoutedEventArgs e) { await SetGpdOutputAsync(true, "手动"); }
        private async void GpdOff_Click(object sender, RoutedEventArgs e) { await SetGpdOutputAsync(false, "手动"); }

        private Task SetGpdOutputAsync(bool enabled, string source)
        {
            return ExecuteUiAsync("GPD2303S", enabled ? "输出 ON" : "输出 OFF", GpdResultText, GpdErrorText, async token =>
            {
                await _runtime.AuxiliaryPower.SetOutputAsync(enabled, token);
                AddGpdSwitchRecord(enabled, source);
                return "输出实际状态=" + (enabled ? "ON" : "OFF");
            });
        }

        private void GpdCycleStart_Click(object sender, RoutedEventArgs e)
        {
            if (_gpdCycleCts != null)
            {
                GpdCycleStatusText.Text = "循环状态：正在运行";
                return;
            }
            if (_gpdVoltageRampCts != null)
            {
                GpdErrorText.Text = "最近错误：电压升降循环正在运行，请先停止电压循环。";
                return;
            }
            if (!_runtime.AuxiliaryPower.IsConnected)
            {
                GpdErrorText.Text = "最近错误：GPD2303S 尚未连接。";
                return;
            }

            int onMilliseconds;
            int offMilliseconds;
            try
            {
                onMilliseconds = ParseMilliseconds(GpdCycleOnMsText.Text, "ON 持续时间");
                offMilliseconds = ParseMilliseconds(GpdCycleOffMsText.Text, "OFF 持续时间");
            }
            catch (Exception exception)
            {
                GpdErrorText.Text = "最近错误：" + exception.Message;
                return;
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _gpdCycleCts = cts;
            RefreshGpdOperationUi();
            GpdErrorText.Text = "最近错误：--";
            GpdCycleStatusText.Text = string.Format(CultureInfo.InvariantCulture, "循环状态：启动中 · ON {0} ms / OFF {1} ms", onMilliseconds, offMilliseconds);
            GlobalStatusText.Text = "GPD2303S · 循环关断功能运行中";
            _gpdCycleTask = RunGpdCycleAsync(cts, onMilliseconds, offMilliseconds);
        }

        private async void GpdCycleStop_Click(object sender, RoutedEventArgs e)
        {
            await StopGpdCycleAsync();
        }

        private async Task RunGpdCycleAsync(CancellationTokenSource cts, int onMilliseconds, int offMilliseconds)
        {
            var cycleNumber = 0;
            try
            {
                while (true)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    cycleNumber++;
                    await _runtime.AuxiliaryPower.SetOutputAsync(true, cts.Token);
                    AddGpdSwitchRecord(true, "循环第 " + cycleNumber + " 次");
                    GpdCycleStatusText.Text = string.Format(CultureInfo.InvariantCulture, "循环状态：第 {0} 次 ON · 保持 {1} ms", cycleNumber, onMilliseconds);
                    await Task.Delay(onMilliseconds, cts.Token);

                    await _runtime.AuxiliaryPower.SetOutputAsync(false, cts.Token);
                    AddGpdSwitchRecord(false, "循环第 " + cycleNumber + " 次");
                    GpdCycleStatusText.Text = string.Format(CultureInfo.InvariantCulture, "循环状态：第 {0} 次 OFF · 保持 {1} ms", cycleNumber, offMilliseconds);
                    await Task.Delay(offMilliseconds, cts.Token);
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested || _lifetime.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                GpdErrorText.Text = "最近错误：循环关断因错误停止：" + exception.Message;
                await _runtime.Logger.LogAsync(new OperationLogEntry
                {
                    Level = "ERROR",
                    Device = "GPD2303S",
                    Function = "循环关断功能",
                    ErrorCode = "0x" + exception.HResult.ToString("X8", CultureInfo.InvariantCulture),
                    Message = exception.Message,
                    RawResponse = exception.ToString()
                });
            }
            finally
            {
                var outputClosed = false;
                if (_runtime.AuxiliaryPower.IsConnected)
                {
                    try
                    {
                        using (var safetyCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                        {
                            await _runtime.AuxiliaryPower.SetOutputAsync(false, safetyCts.Token);
                        }
                        AddGpdSwitchRecord(false, "循环停止安全关断");
                        outputClosed = true;
                    }
                    catch (Exception exception)
                    {
                        GpdErrorText.Text = "最近错误：循环已停止，但输出安全关闭失败：" + exception.Message;
                    }
                }

                if (ReferenceEquals(_gpdCycleCts, cts)) _gpdCycleCts = null;
                RefreshGpdOperationUi();
                GpdCycleStatusText.Text = outputClosed ? "循环状态：已停止，输出已关闭" : "循环状态：已停止";
                GlobalStatusText.Text = "GPD2303S · 循环关断功能已停止";
                cts.Dispose();
            }
        }

        private async Task StopGpdCycleAsync()
        {
            var cts = _gpdCycleCts;
            if (cts == null)
            {
                RefreshGpdOperationUi();
                return;
            }

            GpdCycleStatusText.Text = "循环状态：正在停止并关闭输出…";
            cts.Cancel();
            try
            {
                await _gpdCycleTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void RefreshGpdOperationUi()
        {
            var outputCycleRunning = _gpdCycleCts != null;
            var voltageRampRunning = _gpdVoltageRampCts != null;
            var anyAutomaticOperation = outputCycleRunning || voltageRampRunning;

            GpdCycleStartButton.IsEnabled = !anyAutomaticOperation;
            GpdCycleStopButton.IsEnabled = outputCycleRunning;
            GpdCycleOnMsText.IsEnabled = !anyAutomaticOperation;
            GpdCycleOffMsText.IsEnabled = !anyAutomaticOperation;

            GpdRampStartButton.IsEnabled = !anyAutomaticOperation;
            GpdRampStopButton.IsEnabled = voltageRampRunning;
            GpdRampChannelBox.IsEnabled = !anyAutomaticOperation;
            GpdRampHighVoltageText.IsEnabled = !anyAutomaticOperation;
            GpdRampLowVoltageText.IsEnabled = !anyAutomaticOperation;
            GpdRampRiseRateText.IsEnabled = !anyAutomaticOperation;
            GpdRampFallRateText.IsEnabled = !anyAutomaticOperation;
            GpdRampCycleCountText.IsEnabled = !anyAutomaticOperation;

            GpdChannelBox.IsEnabled = !anyAutomaticOperation;
            GpdVoltageText.IsEnabled = !anyAutomaticOperation;
            GpdCurrentText.IsEnabled = !anyAutomaticOperation;
            GpdSetButton.IsEnabled = !anyAutomaticOperation;
            GpdManualOnButton.IsEnabled = !anyAutomaticOperation;
            GpdManualOffButton.IsEnabled = !anyAutomaticOperation;
            GpdReadButton.IsEnabled = !anyAutomaticOperation;
        }

        private void GpdRampStart_Click(object sender, RoutedEventArgs e)
        {
            if (_gpdVoltageRampCts != null)
            {
                GpdRampStatusText.Text = "电压循环：正在运行";
                return;
            }
            if (_gpdCycleCts != null)
            {
                GpdErrorText.Text = "最近错误：循环关断功能正在运行，请先停止循环关断。";
                return;
            }
            if (!_runtime.AuxiliaryPower.IsConnected)
            {
                GpdErrorText.Text = "最近错误：GPD2303S 尚未连接。";
                return;
            }

            int channel;
            decimal highVoltage;
            decimal lowVoltage;
            decimal riseRate;
            decimal fallRate;
            int cycleCount;
            try
            {
                channel = SelectedNumber(GpdRampChannelBox);
                highVoltage = ParseGpdRampValue(GpdRampHighVoltageText.Text, "上升目标电压", 0.001m, 30m, true);
                lowVoltage = ParseGpdRampValue(GpdRampLowVoltageText.Text, "下降目标电压", 0m, 29.999m, true);
                riseRate = ParseGpdRampValue(GpdRampRiseRateText.Text, "上升速率", 0.001m, 30m, false);
                fallRate = ParseGpdRampValue(GpdRampFallRateText.Text, "下降速率", 0.001m, 30m, false);
                cycleCount = ParseInteger(GpdRampCycleCountText.Text, "循环次数", 1, 100000);
                if (highVoltage <= lowVoltage) throw new InvalidOperationException("上升目标电压必须大于下降目标电压。");
            }
            catch (Exception exception)
            {
                GpdErrorText.Text = "最近错误：" + exception.Message;
                return;
            }

            GpdRampHighVoltageText.Text = highVoltage.ToString("0.###", CultureInfo.InvariantCulture);
            GpdRampLowVoltageText.Text = lowVoltage.ToString("0.###", CultureInfo.InvariantCulture);
            GpdRampRiseRateText.Text = riseRate.ToString("0.###", CultureInfo.InvariantCulture);
            GpdRampFallRateText.Text = fallRate.ToString("0.###", CultureInfo.InvariantCulture);
            GpdRampCycleCountText.Text = cycleCount.ToString(CultureInfo.InvariantCulture);
            GpdChannelBox.SelectedIndex = channel - 1;

            var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _gpdVoltageRampCts = cts;
            RefreshGpdOperationUi();
            GpdErrorText.Text = "最近错误：--";
            GpdRampStatusText.Text = string.Format(CultureInfo.InvariantCulture,
                "电压循环：启动中 · CH{0} · {1}→{2}→{1} V · {3} 次", channel, lowVoltage, highVoltage, cycleCount);
            GlobalStatusText.Text = "GPD2303S · 电压升降循环运行中";
            _gpdVoltageRampTask = RunGpdVoltageRampAsync(cts, channel, lowVoltage, highVoltage, riseRate, fallRate, cycleCount);
        }

        private async void GpdRampStop_Click(object sender, RoutedEventArgs e)
        {
            await StopGpdVoltageRampAsync();
        }

        private async Task RunGpdVoltageRampAsync(CancellationTokenSource cts, int channel, decimal lowVoltage, decimal highVoltage, decimal riseRate, decimal fallRate, int cycleCount)
        {
            var completedCycles = 0;
            var currentVoltage = 0m;
            try
            {
                await _runtime.Logger.LogAsync(new OperationLogEntry
                {
                    Level = "INFO",
                    Device = "GPD2303S",
                    Function = "电压升降循环",
                    Parameters = string.Format(CultureInfo.InvariantCulture, "CH{0}; Low={1}V; High={2}V; Rise={3}V/s; Fall={4}V/s; Count={5}", channel, lowVoltage, highVoltage, riseRate, fallRate, cycleCount),
                    Message = "电压升降循环已启动"
                });

                var reading = await _runtime.AuxiliaryPower.ReadAsync(channel, cts.Token);
                currentVoltage = decimal.Round(reading.SetVoltage, 3, MidpointRounding.AwayFromZero);
                if (currentVoltage != lowVoltage)
                {
                    var preparationRate = currentVoltage < lowVoltage ? riseRate : fallRate;
                    currentVoltage = await RampGpdVoltageSegmentAsync(cts.Token, channel, currentVoltage, lowVoltage, preparationRate, 0, cycleCount, "准备");
                }

                for (var cycle = 1; cycle <= cycleCount; cycle++)
                {
                    currentVoltage = await RampGpdVoltageSegmentAsync(cts.Token, channel, lowVoltage, highVoltage, riseRate, cycle, cycleCount, "上升");
                    currentVoltage = await RampGpdVoltageSegmentAsync(cts.Token, channel, highVoltage, lowVoltage, fallRate, cycle, cycleCount, "下降");
                    completedCycles = cycle;
                }

                GpdRampStatusText.Text = string.Format(CultureInfo.InvariantCulture, "电压循环：已完成 · CH{0} · {1} 次 · 当前 {2:0.###} V", channel, completedCycles, currentVoltage);
                GpdResultText.Text = string.Format(CultureInfo.InvariantCulture, "最近结果：CH{0} 电压升降循环已完成 {1} 次，停在 {2:0.###} V", channel, completedCycles, currentVoltage);
                GlobalStatusText.Text = "GPD2303S · 电压升降循环完成";
                await _runtime.Logger.LogAsync(new OperationLogEntry
                {
                    Level = "INFO",
                    Device = "GPD2303S",
                    Function = "电压升降循环",
                    Parameters = "CH" + channel,
                    Message = string.Format(CultureInfo.InvariantCulture, "已完成 {0} 次，最终电压 {1:0.###} V", completedCycles, currentVoltage)
                });
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested || _lifetime.IsCancellationRequested)
            {
                GpdRampStatusText.Text = string.Format(CultureInfo.InvariantCulture, "电压循环：已停止 · 已完成 {0}/{1} 次 · 保持 {2:0.###} V", completedCycles, cycleCount, currentVoltage);
                GlobalStatusText.Text = "GPD2303S · 电压升降循环已停止";
                await _runtime.Logger.LogAsync(new OperationLogEntry
                {
                    Level = "INFO",
                    Device = "GPD2303S",
                    Function = "停止电压升降循环",
                    Parameters = "CH" + channel,
                    Message = string.Format(CultureInfo.InvariantCulture, "已完成 {0}/{1} 次，保持 {2:0.###} V", completedCycles, cycleCount, currentVoltage)
                });
            }
            catch (Exception exception)
            {
                GpdRampStatusText.Text = string.Format(CultureInfo.InvariantCulture, "电压循环：因错误停止 · 已完成 {0}/{1} 次 · 最近设定 {2:0.###} V", completedCycles, cycleCount, currentVoltage);
                GpdErrorText.Text = "最近错误：电压升降循环失败：" + exception.Message;
                GlobalStatusText.Text = "GPD2303S · 电压升降循环失败";
                await _runtime.Logger.LogAsync(new OperationLogEntry
                {
                    Level = "ERROR",
                    Device = "GPD2303S",
                    Function = "电压升降循环",
                    Parameters = "CH" + channel,
                    ErrorCode = "0x" + exception.HResult.ToString("X8", CultureInfo.InvariantCulture),
                    Message = exception.Message,
                    RawResponse = exception.ToString()
                });
            }
            finally
            {
                if (ReferenceEquals(_gpdVoltageRampCts, cts)) _gpdVoltageRampCts = null;
                _gpdVoltageRampTask = Task.CompletedTask;
                RefreshGpdOperationUi();
                cts.Dispose();
            }
        }

        private async Task<decimal> RampGpdVoltageSegmentAsync(CancellationToken cancellationToken, int channel, decimal startVoltage, decimal targetVoltage, decimal rate, int cycle, int cycleCount, string phase)
        {
            startVoltage = decimal.Round(startVoltage, 3, MidpointRounding.AwayFromZero);
            targetVoltage = decimal.Round(targetVoltage, 3, MidpointRounding.AwayFromZero);
            var distance = Math.Abs(targetVoltage - startVoltage);
            if (distance == 0m) return targetVoltage;

            var direction = targetVoltage > startVoltage ? 1m : -1m;
            var durationSeconds = distance / rate;
            var lastCommandedVoltage = startVoltage;
            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var elapsedSeconds = Convert.ToDecimal(stopwatch.Elapsed.TotalSeconds, CultureInfo.InvariantCulture);
                if (elapsedSeconds >= durationSeconds) break;
                var remainingMilliseconds = (int)Math.Ceiling((double)((durationSeconds - elapsedSeconds) * 1000m));
                await Task.Delay(Math.Max(1, Math.Min(100, remainingMilliseconds)), cancellationToken);
                elapsedSeconds = Convert.ToDecimal(stopwatch.Elapsed.TotalSeconds, CultureInfo.InvariantCulture);
                var travelled = Math.Min(distance, rate * elapsedSeconds);
                var nextVoltage = decimal.Round(startVoltage + direction * travelled, 3, MidpointRounding.AwayFromZero);
                if ((direction > 0m && nextVoltage > targetVoltage) || (direction < 0m && nextVoltage < targetVoltage)) nextVoltage = targetVoltage;
                if (nextVoltage == lastCommandedVoltage) continue;
                await _runtime.AuxiliaryPower.SetVoltageAsync(channel, nextVoltage, cancellationToken);
                lastCommandedVoltage = nextVoltage;
                GpdVoltageText.Text = nextVoltage.ToString("0.###", CultureInfo.InvariantCulture);
                GpdRampStatusText.Text = cycle == 0
                    ? string.Format(CultureInfo.InvariantCulture, "电压循环：准备 · CH{0} · {1:0.###}/{2:0.###} V · {3:0.###} V/s", channel, nextVoltage, targetVoltage, rate)
                    : string.Format(CultureInfo.InvariantCulture, "电压循环：第 {0}/{1} 次 {2} · CH{3} · {4:0.###}/{5:0.###} V · {6:0.###} V/s", cycle, cycleCount, phase, channel, nextVoltage, targetVoltage, rate);
            }

            if (lastCommandedVoltage != targetVoltage)
            {
                await _runtime.AuxiliaryPower.SetVoltageAsync(channel, targetVoltage, cancellationToken);
                GpdVoltageText.Text = targetVoltage.ToString("0.###", CultureInfo.InvariantCulture);
            }
            return targetVoltage;
        }

        private async Task StopGpdVoltageRampAsync()
        {
            var cts = _gpdVoltageRampCts;
            var task = _gpdVoltageRampTask;
            if (cts == null)
            {
                RefreshGpdOperationUi();
                return;
            }

            GpdRampStatusText.Text = "电压循环：正在停止，保持当前电压…";
            cts.Cancel();
            try { await task; } catch (OperationCanceledException) { }
        }

        private static decimal ParseGpdRampValue(string text, string name, decimal minimum, decimal maximum, bool allowZero)
        {
            var value = decimal.Round(ParseDecimal(text, name), 3, MidpointRounding.AwayFromZero);
            if ((!allowZero && value <= 0m) || value < minimum || value > maximum)
                throw new ArgumentOutOfRangeException(name, string.Format(CultureInfo.InvariantCulture, "{0}必须为 {1:0.###}～{2:0.###}{3}。", name, minimum, maximum, name.IndexOf("速率", StringComparison.Ordinal) >= 0 ? " V/s" : " V"));
            return value;
        }

        private void AddGpdSwitchRecord(bool enabled, string source)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            _gpdSwitchRecords.Add(string.Format(CultureInfo.InvariantCulture, "{0}  {1,-3}  {2}", timestamp, enabled ? "ON" : "OFF", source));
            while (_gpdSwitchRecords.Count > 2000) _gpdSwitchRecords.RemoveAt(0);
            if (_gpdSwitchRecords.Count > 0) GpdSwitchRecordList.ScrollIntoView(_gpdSwitchRecords[_gpdSwitchRecords.Count - 1]);
        }

        private async void GpdRead_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteUiAsync("GPD2303S", "读取", GpdResultText, GpdErrorText, async token => (await _runtime.AuxiliaryPower.ReadAsync(SelectedNumber(GpdChannelBox), token)).ToString());
        }

        private async void LoadConnect_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteUiAsync("N69206", "连接", LoadResultText, LoadErrorText, async token =>
            {
                if (!_runtime.ElectronicLoad.IsConnected)
                {
                    ApplyLoadConfigFromUi(true);
                    await _runtime.ElectronicLoad.ConnectAsync(token);
                }
                LoadStatusText.Text = "已连接";
                var config = _runtime.Config.ElectronicLoad;
                return string.Format(CultureInfo.InvariantCulture, "连接成功：{0}:{1} · ID {2}", config.Host, config.Port, config.DeviceId);
            });
        }
        private async void LoadDisconnect_Click(object sender, RoutedEventArgs e) { await DisconnectAsync("N69206", _runtime.ElectronicLoad, LoadStatusText, LoadResultText, LoadErrorText); }

        private void LoadModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LoadValueLabel != null) RefreshLoadModeUi();
        }

        private void LoadRangeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LoadRangeText == null || LoadModeBox == null) return;
            ApplyLoadRangesFromUi(false);
            RefreshLoadModeUi();
        }

        private void LoadVoltageRateBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LoadCustomRateText == null || LoadVoltageRateBox.SelectedItem == null) return;
            var custom = (ElectronicLoadVoltageRate)LoadVoltageRateBox.SelectedItem == ElectronicLoadVoltageRate.UserDefined;
            LoadCustomRateLabel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
            LoadCustomRateText.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void LoadConfigure_Click(object sender, RoutedEventArgs e)
        {
            var mode = (ElectronicLoadMode)LoadModeBox.SelectedItem;
            await ExecuteUiAsync("N69206", "配置 " + LoadModeName(mode), LoadResultText, LoadErrorText, async token =>
            {
                ApplyLoadConfigFromUi(false);
                var value = ParseDecimal(LoadValueText.Text, LoadValueLabel.Text);
                var config = _runtime.Config.ElectronicLoad;
                var range = LoadRange(config, mode);
                if (mode == ElectronicLoadMode.ConstantVoltage)
                {
                    var rate = (ElectronicLoadVoltageRate)LoadVoltageRateBox.SelectedItem;
                    decimal? customRate = rate == ElectronicLoadVoltageRate.UserDefined ? ParseDecimal(LoadCustomRateText.Text, "自定义 V-Rate") : (decimal?)null;
                    await _runtime.ElectronicLoad.ConfigureConstantVoltageAsync(value, range, rate, customRate, token);
                    return string.Format(CultureInfo.InvariantCulture, "CV={0} V，量程={1}，V-Rate={2}{3}", value, LoadRangeName(range), rate, customRate.HasValue ? " / " + customRate.Value + " V/ms" : string.Empty);
                }
                var rise = ParseDecimal(LoadRiseText.Text, "Rise Slew");
                var fall = ParseDecimal(LoadFallText.Text, "Fall Slew");
                if (mode == ElectronicLoadMode.ConstantCurrent) await _runtime.ElectronicLoad.ConfigureConstantCurrentAsync(value, range, rise, fall, token);
                else if (mode == ElectronicLoadMode.ConstantResistance) await _runtime.ElectronicLoad.ConfigureConstantResistanceAsync(value, range, rise, fall, token);
                else await _runtime.ElectronicLoad.ConfigureConstantPowerAsync(value, range, rise, fall, token);
                return string.Format(CultureInfo.InvariantCulture, "{0}={1} {2}，量程={3}，Rise/Fall={4}/{5} A/ms", LoadModeName(mode), value, LoadValueUnitText.Text, LoadRangeName(range), rise, fall);
            });
        }

        private async void LoadOn_Click(object sender, RoutedEventArgs e) { await SetLoadInputAsync(true); }
        private async void LoadOff_Click(object sender, RoutedEventArgs e) { await SetLoadInputAsync(false); }

        private Task SetLoadInputAsync(bool enabled)
        {
            return ExecuteUiAsync("N69206", enabled ? "加载 ON" : "加载 OFF", LoadResultText, LoadErrorText, async token =>
            {
                await _runtime.ElectronicLoad.SetInputAsync(enabled, token);
                return "加载实际状态=" + (enabled ? "ON" : "OFF");
            });
        }

        private async void LoadRead_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteUiAsync("N69206", "读取测量", LoadResultText, LoadErrorText, async token => (await _runtime.ElectronicLoad.ReadAsync(token)).ToString());
        }

        private async void DmmConnect_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteUiAsync("GDM9061", "连接", DmmResultText, DmmErrorText, async token =>
            {
                if (!_runtime.Multimeter.IsConnected)
                {
                    ApplySerialConfigFromUi(_runtime.Config.Multimeter, DmmPortText, DmmBaudBox, "GDM9061");
                    await _runtime.Multimeter.ConnectAsync(token);
                }
                DmmStatusText.Text = "已连接";
                return string.Format(CultureInfo.InvariantCulture, "连接成功：{0} / {1} / 8N1 / CRLF", _runtime.Config.Multimeter.PortName, _runtime.Config.Multimeter.BaudRate);
            });
        }
        private async void DmmDisconnect_Click(object sender, RoutedEventArgs e) { await DisconnectAsync("GDM9061", _runtime.Multimeter, DmmStatusText, DmmResultText, DmmErrorText); }

        private void DmmFunctionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DmmRangeBox != null) RefreshDmmRanges();
        }

        private async void DmmConfigure_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteUiAsync("GDM9061", "配置测量", DmmResultText, DmmErrorText, async token =>
            {
                var function = (MultimeterFunction)DmmFunctionBox.SelectedItem;
                var choice = (DmmRangeChoice)DmmRangeBox.SelectedItem;
                decimal? range = choice.Value;
                await _runtime.Multimeter.ConfigureAsync(function, range, token);
                return function + "，量程 " + choice.Label;
            });
        }

        private async void DmmRead_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteUiAsync("GDM9061", "读取", DmmResultText, DmmErrorText, async token => (await _runtime.Multimeter.ReadAsync(token)).ToString());
        }

        private async void PowerConnect_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteUiAsync("PA333H", "连接", PowerResultText, PowerErrorText, async token =>
            {
                if (!_runtime.PowerMeter.IsConnected)
                {
                    ApplyPowerConfigFromUi(true);
                    await _runtime.PowerMeter.ConnectAsync(token);
                }
                PowerStatusText.Text = "已连接";
                return string.Format(CultureInfo.InvariantCulture, "连接成功：{0} / {1} / 8N1 / LF", _runtime.Config.PowerMeter.PortName, _runtime.Config.PowerMeter.BaudRate);
            });
        }
        private async void PowerDisconnect_Click(object sender, RoutedEventArgs e)
        {
            StopPowerContinuous();
            await DisconnectAsync("PA333H", _runtime.PowerMeter, PowerStatusText, PowerResultText, PowerErrorText);
        }

        private async void PowerConfigure_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteUiAsync("PA333H", "应用通道配置", PowerResultText, PowerErrorText, async token =>
            {
                ApplyPowerConfigFromUi(false);
                await _runtime.PowerMeter.ConfigureAsync(token);
                return "已配置 U/I/P：CH" + _runtime.Config.PowerMeter.SourceChannel + " 源侧，CH" + _runtime.Config.PowerMeter.OutputChannel + " 输出侧";
            });
        }

        private async void PowerRead_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteUiAsync("PA333H", "读取六项功率数据", PowerResultText, PowerErrorText, async token => (await _runtime.PowerMeter.ReadAsync(token)).ToString());
        }

        private void PowerStartContinuous_Click(object sender, RoutedEventArgs e)
        {
            if (_powerContinuousCts != null)
            {
                PowerContinuousStatusText.Text = "连续读取：正在运行";
                return;
            }
            if (!_runtime.PowerMeter.IsConnected)
            {
                PowerErrorText.Text = "最近错误：PA333H 尚未连接。";
                return;
            }
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _powerContinuousCts = cts;
            PowerContinuousStatusText.Text = "连续读取：正在运行（界面可继续操作）";
            var ignored = RunPowerContinuousAsync(cts, SelectedNumber(PowerIntervalBox));
        }

        private void PowerStopContinuous_Click(object sender, RoutedEventArgs e)
        {
            StopPowerContinuous();
        }

        private async Task RunPowerContinuousAsync(CancellationTokenSource cts, int intervalMilliseconds)
        {
            try
            {
                while (true)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    var reading = await _runtime.PowerMeter.ReadAsync(cts.Token);
                    PowerResultText.Text = "连续读数：" + reading;
                    PowerContinuousStatusText.Text = "连续读取：运行中 · " + DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
                    await Task.Delay(intervalMilliseconds, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                PowerContinuousStatusText.Text = "连续读取：已停止";
            }
            catch (Exception exception)
            {
                PowerContinuousStatusText.Text = "连续读取：因错误停止";
                PowerErrorText.Text = "最近错误：" + exception.Message;
                await _runtime.Logger.LogAsync(new OperationLogEntry { Level = "ERROR", Device = "PA333H", Function = "连续读取", Message = exception.Message, ErrorCode = "0x" + exception.HResult.ToString("X8", CultureInfo.InvariantCulture), RawResponse = exception.ToString() });
            }
            finally
            {
                if (ReferenceEquals(_powerContinuousCts, cts)) _powerContinuousCts = null;
                cts.Dispose();
            }
        }

        private void StopPowerContinuous()
        {
            var cts = _powerContinuousCts;
            if (cts == null)
            {
                PowerContinuousStatusText.Text = "连续读取：已停止";
                return;
            }
            cts.Cancel();
            PowerContinuousStatusText.Text = "连续读取：正在停止…";
        }

        private void CanAdapterModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CanChannelBox == null) return;
            RefreshCanChannelChoices();
        }

        private void CanBusModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshCanFrameTypeUi();
        }

        private void CanFdFrameCheck_Changed(object sender, RoutedEventArgs e)
        {
            RefreshCanFrameTypeUi();
        }

        private void RefreshCanFrameTypeUi()
        {
            if (CanBusModeBox == null || CanDataBitRateBox == null || CanFdFrameCheck == null || CanBrsCheck == null || CanDataLengthBox == null) return;
            var canFdMode = string.Equals(ComboBoxText(CanBusModeBox), "CAN FD", StringComparison.OrdinalIgnoreCase);
            var periodicSending = _canPeriodicSendCts != null;
            CanDataBitRateBox.IsEnabled = canFdMode && !_runtime.DcdcCan.IsConnected;
            CanFdFrameCheck.IsEnabled = canFdMode && !periodicSending;
            if (!canFdMode) CanFdFrameCheck.IsChecked = false;
            CanBrsCheck.IsEnabled = canFdMode && CanFdFrameCheck.IsChecked == true && !periodicSending;
            if (!CanBrsCheck.IsEnabled) CanBrsCheck.IsChecked = false;
            var canFdFrame = CanFdFrameCheck.IsChecked == true;
            int previousLength;
            if (!int.TryParse(CanDataLengthBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out previousLength)) previousLength = 8;
            var lengths = canFdFrame
                ? new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 12, 16, 20, 24, 32, 48, 64 }
                : Enumerable.Range(0, 9).ToArray();
            if (!lengths.Contains(previousLength)) previousLength = 8;
            CanDataLengthBox.ItemsSource = lengths;
            CanDataLengthBox.SelectedItem = previousLength;
            CanDataLengthBox.Text = previousLength.ToString(CultureInfo.InvariantCulture);
        }

        private void RefreshCanChannelChoices()
        {
            if (CanAdapterModelBox == null || CanChannelBox == null) return;
            var model = ComboBoxText(CanAdapterModelBox);
            var channelCount = model.IndexOf("100U", StringComparison.OrdinalIgnoreCase) >= 0 ? 1
                : model.IndexOf("VN1640A", StringComparison.OrdinalIgnoreCase) >= 0 ? 4 : 2;
            var previous = CanChannelBox.SelectedItem == null ? 0 : SelectedNumber(CanChannelBox);
            CanChannelBox.ItemsSource = Enumerable.Range(0, channelCount).ToArray();
            CanChannelBox.SelectedItem = Math.Min(previous, channelCount - 1);
        }

        private void ApplyCanConfigFromUi()
        {
            var model = ComboBoxText(CanAdapterModelBox);
            if (model.IndexOf("VN1640A", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new NotSupportedException("Vector VN1640A 选项已保留，当前版本尚未接入 Vector XL Driver Library；请选择 ZLG 100U 或 200U。");

            var config = _runtime.Config.Can;
            config.AdapterModel = model;
            config.DeviceIndex = ParseInteger(CanDeviceIndexText.Text, "CAN 设备索引", 0, 255);
            config.ChannelCount = model.IndexOf("100U", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 2;
            config.ChannelIndex = SelectedNumber(CanChannelBox);
            config.UseClassicCan = !string.Equals(ComboBoxText(CanBusModeBox), "CAN FD", StringComparison.OrdinalIgnoreCase);
            var arbitrationKbps = ParseInteger(ComboBoxText(CanArbitrationBitRateBox), "CAN 仲裁波特率（kbps）", 50, 1000);
            var dataKbps = ParseInteger(ComboBoxText(CanDataBitRateBox), "CAN FD 数据波特率（kbps）", 100, 5000);
            config.ArbitrationBitRate = checked(arbitrationKbps * 1000);
            config.DataBitRate = checked(dataKbps * 1000);
            config.TerminationEnabled = CanTerminationCheck.IsChecked == true;
            CanConfigText.Text = config.UseClassicCan
                ? string.Format(CultureInfo.InvariantCulture, "当前界面：{0} / 索引 {1} / 经典 CAN / 仲裁 {2} kbps / 120Ω={3}；可单独或同时打开通道", config.AdapterModel, config.DeviceIndex, arbitrationKbps, config.TerminationEnabled)
                : string.Format(CultureInfo.InvariantCulture, "当前界面：{0} / 索引 {1} / CAN FD / 仲裁 {2} kbps / 数据 {3} kbps / 120Ω={4}；可单独或同时打开通道", config.AdapterModel, config.DeviceIndex, arbitrationKbps, dataKbps, config.TerminationEnabled);
        }

        private void SetCanConnectionSettingsEnabled(bool enabled)
        {
            CanAdapterModelBox.IsEnabled = enabled;
            CanDeviceIndexText.IsEnabled = enabled;
            CanBusModeBox.IsEnabled = enabled;
            CanArbitrationBitRateBox.IsEnabled = enabled;
            CanTerminationCheck.IsEnabled = enabled;
            CanDataBitRateBox.IsEnabled = enabled && string.Equals(ComboBoxText(CanBusModeBox), "CAN FD", StringComparison.OrdinalIgnoreCase);
            RefreshCanFrameTypeUi();
        }

        private async void CanConnect_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteUiAsync("CAN", "连接适配器", CanResultText, CanErrorText, async token =>
            {
                if (!_runtime.DcdcCan.IsConnected)
                {
                    ApplyCanConfigFromUi();
                    await _runtime.DcdcCan.ConnectAsync(token);
                }
                return "适配器连接成功；CAN0～CAN" + (_runtime.Config.Can.ChannelCount - 1) + " 已完成预配置，可单独或同时打开";
            });
            RefreshCanConnectionState();
        }
        private async void CanDisconnect_Click(object sender, RoutedEventArgs e)
        {
            await StopCanPeriodicSendAsync();
            await StopCanReceiveLoopAsync();
            await DisconnectAsync("CAN", _runtime.DcdcCan, CanStatusText, CanResultText, CanErrorText);
            RefreshCanConnectionState();
        }
        private async void CanOpenChannel_Click(object sender, RoutedEventArgs e)
        {
            var channel = SelectedNumber(CanChannelBox);
            await ExecuteUiAsync("CAN", "打开 CAN" + channel, CanResultText, CanErrorText, async token =>
            {
                await _runtime.DcdcCan.OpenChannelAsync(channel, token);
                return "CAN" + channel + " 已按当前界面参数打开，可发送和接收报文";
            });
            RefreshCanConnectionState();
        }
        private async void CanCloseChannel_Click(object sender, RoutedEventArgs e)
        {
            var channel = SelectedNumber(CanChannelBox);
            await StopCanPeriodicSendAsync();
            await StopCanReceiveLoopAsync();
            await ExecuteUiAsync("CAN", "关闭 CAN" + channel, CanResultText, CanErrorText, async token =>
            {
                await _runtime.DcdcCan.CloseChannelAsync(channel, token);
                return "CAN" + channel + " 已独立关闭";
            });
            RefreshCanConnectionState();
        }

        private async void CanOpenAllChannels_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteUiAsync("CAN", "同时打开全部通道", CanResultText, CanErrorText, async token =>
            {
                var previouslyOpen = new HashSet<int>(_runtime.DcdcCan.OpenChannels);
                var openedNow = new List<int>();
                try
                {
                    for (var channel = 0; channel < _runtime.Config.Can.ChannelCount; channel++)
                    {
                        await _runtime.DcdcCan.OpenChannelAsync(channel, token);
                        if (!previouslyOpen.Contains(channel)) openedNow.Add(channel);
                    }
                }
                catch
                {
                    foreach (var channel in openedNow.OrderByDescending(value => value))
                    {
                        try { await _runtime.DcdcCan.CloseChannelAsync(channel, CancellationToken.None); } catch { }
                    }
                    throw;
                }
                return string.Join("、", _runtime.DcdcCan.OpenChannels.Select(value => "CAN" + value)) + " 已同时打开，可分别发送和接收报文";
            });
            RefreshCanConnectionState();
        }

        private async void CanCloseAllChannels_Click(object sender, RoutedEventArgs e)
        {
            await StopCanPeriodicSendAsync();
            await StopCanReceiveLoopAsync();
            await ExecuteUiAsync("CAN", "关闭全部通道", CanResultText, CanErrorText, async token =>
            {
                var failures = new List<string>();
                foreach (var channel in _runtime.DcdcCan.OpenChannels.OrderByDescending(value => value).ToArray())
                {
                    try { await _runtime.DcdcCan.CloseChannelAsync(channel, token); }
                    catch (Exception exception) { failures.Add("CAN" + channel + "：" + exception.Message); }
                }
                if (failures.Count != 0) throw new InvalidOperationException("通道状态已清除，但驱动停止返回失败：" + string.Join("；", failures));
                return "全部 CAN 通道已关闭";
            });
            RefreshCanConnectionState();
        }
        private async void CanSend_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteUiAsync("CAN", "单帧/循环发送", CanResultText, CanErrorText, async token =>
            {
                var channel = SelectedNumber(CanChannelBox);
                var frame = ReadCanFrameFromUi(channel);
                var intervalMilliseconds = ParseInteger(CanSendIntervalText.Text, "CAN 发送间隔", 0, 86400000);
                if (intervalMilliseconds == 0)
                {
                    await _runtime.DcdcCan.SendAsync(channel, frame, token);
                    AppendCanFrame("TX", frame);
                    return frame + "；间隔 0 ms，仅发送一次";
                }
                if (_canPeriodicSendCts != null) throw new InvalidOperationException("循环发送已在运行，请先点击“停止循环发送”。");
                var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _canPeriodicSendCts = cts;
                SetCanPeriodicSendUiRunning(true);
                _canPeriodicSendTask = RunCanPeriodicSendAsync(cts, channel, frame, intervalMilliseconds);
                return string.Format(CultureInfo.InvariantCulture, "CAN{0} 已启动循环发送，ID={1:X}，长度={2}，间隔={3} ms", channel, frame.Id, frame.Data.Length, intervalMilliseconds);
            });
            RefreshCanConnectionState();
        }

        private CanFrame ReadCanFrameFromUi(int channel)
        {
            uint id;
            if (!uint.TryParse(CanIdText.Text.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id)) throw new FormatException("CAN ID 必须为十六进制。");
            var isCanFd = CanFdFrameCheck.IsChecked == true;
            var maximumLength = isCanFd ? 64 : 8;
            var dataLength = ParseInteger(ComboBoxText(CanDataLengthBox), "CAN 数据长度", 0, maximumLength);
            if (isCanFd && !new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 12, 16, 20, 24, 32, 48, 64 }.Contains(dataLength))
                throw new ArgumentOutOfRangeException("CAN 数据长度", "CAN FD 数据长度必须为 0～8、12、16、20、24、32、48 或 64 字节。");
            var entered = CanTextCodec.ParseData(CanDataText.Text);
            if (entered.Length > dataLength)
                throw new InvalidOperationException("已输入 " + entered.Length + " 字节，但数据长度设置为 " + dataLength + "；请增大长度或减少数据。");
            var data = new byte[dataLength];
            Array.Copy(entered, data, entered.Length);
            return new CanFrame
            {
                Id = id,
                IsExtended = CanExtendedCheck.IsChecked == true,
                IsCanFd = isCanFd,
                BitRateSwitch = CanBrsCheck.IsChecked == true,
                Data = data,
                ChannelIndex = channel
            };
        }

        private async Task RunCanPeriodicSendAsync(CancellationTokenSource cts, int channel, CanFrame frame, int intervalMilliseconds)
        {
            var sentCount = 0L;
            try
            {
                while (true)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    var stopwatch = Stopwatch.StartNew();
                    await _runtime.DcdcCan.SendAsync(channel, frame, cts.Token);
                    AppendCanFrame("TX", frame);
                    sentCount++;
                    CanPeriodicSendStatusText.Text = string.Format(CultureInfo.InvariantCulture, "循环发送：运行中 · CAN{0} · {1} ms · 已发送 {2} 帧", channel, intervalMilliseconds, sentCount);
                    var remaining = intervalMilliseconds - (int)stopwatch.ElapsedMilliseconds;
                    if (remaining > 0) await Task.Delay(remaining, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                CanPeriodicSendStatusText.Text = "循环发送：已停止 · 共发送 " + sentCount + " 帧";
            }
            catch (Exception exception)
            {
                CanPeriodicSendStatusText.Text = "循环发送：因错误停止 · 共发送 " + sentCount + " 帧";
                CanErrorText.Text = "最近错误：" + exception.Message;
                GlobalStatusText.Text = "CAN · 循环发送失败";
                await _runtime.Logger.LogAsync(new OperationLogEntry
                {
                    Level = "ERROR",
                    Device = "CAN",
                    Function = "循环发送",
                    ErrorCode = "0x" + exception.HResult.ToString("X8", CultureInfo.InvariantCulture),
                    Message = exception.Message,
                    RawResponse = exception.ToString()
                });
            }
            finally
            {
                if (ReferenceEquals(_canPeriodicSendCts, cts))
                {
                    _canPeriodicSendCts = null;
                    _canPeriodicSendTask = Task.CompletedTask;
                    SetCanPeriodicSendUiRunning(false);
                }
                cts.Dispose();
            }
        }

        private async void CanStopPeriodicSend_Click(object sender, RoutedEventArgs e)
        {
            await StopCanPeriodicSendAsync();
            CanResultText.Text = "最近结果：循环发送已停止";
        }

        private async Task StopCanPeriodicSendAsync()
        {
            var cts = _canPeriodicSendCts;
            var task = _canPeriodicSendTask;
            if (cts == null) return;
            cts.Cancel();
            try { await task; } catch (OperationCanceledException) { }
        }

        private void SetCanPeriodicSendUiRunning(bool running)
        {
            CanSendButton.IsEnabled = !running;
            CanStopPeriodicSendButton.IsEnabled = running;
            CanIdText.IsEnabled = !running;
            CanDataText.IsEnabled = !running;
            CanDataLengthBox.IsEnabled = !running;
            CanSendIntervalText.IsEnabled = !running;
            CanExtendedCheck.IsEnabled = !running;
            CanFdFrameCheck.IsEnabled = !running && string.Equals(ComboBoxText(CanBusModeBox), "CAN FD", StringComparison.OrdinalIgnoreCase);
            CanBrsCheck.IsEnabled = !running && CanFdFrameCheck.IsChecked == true;
            CanChannelBox.IsEnabled = !running;
        }

        private void CanClearFrames_Click(object sender, RoutedEventArgs e)
        {
            _canFrameRecords.Clear();
            _canReceivedFrameCount = 0;
            RenderCanFrames();
            CanReceiveStatusText.Text = _canReceiveCts == null ? "自动接收：等待通道打开" : "自动接收：运行中 · 已清零";
        }

        private void CanFrameFilter_Changed(object sender, RoutedEventArgs e)
        {
            if (CanFramesText == null || CanFrameChannelFilterBox == null || CanFrameDirectionFilterBox == null || CanFrameIdFilterText == null) return;
            RenderCanFrames();
        }

        private void EnsureCanReceiveLoop()
        {
            if (!_runtime.DcdcCan.IsConnected || _runtime.DcdcCan.OpenChannels.Count == 0) return;
            if (_canReceiveCts != null && !_canReceiveTask.IsCompleted) return;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _canReceiveCts = cts;
            CanReceiveStatusText.Text = "自动接收：运行中 · " + string.Join("、", _runtime.DcdcCan.OpenChannels.Select(value => "CAN" + value));
            _canReceiveTask = RunCanReceiveLoopAsync(cts);
        }

        private async Task RunCanReceiveLoopAsync(CancellationTokenSource cts)
        {
            try
            {
                while (true)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    var channels = _runtime.DcdcCan.OpenChannels.ToArray();
                    if (!_runtime.DcdcCan.IsConnected || channels.Length == 0) break;
                    var receivedAny = false;
                    foreach (var channel in channels)
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        var frames = await _runtime.DcdcCan.ReceiveAsync(channel, 256, 0, cts.Token);
                        if (frames.Count == 0) continue;
                        receivedAny = true;
                        AppendCanFrames("RX", frames);
                        _canReceivedFrameCount += frames.Count;
                        CanReceiveStatusText.Text = string.Format(CultureInfo.InvariantCulture, "自动接收：运行中 · 累计 {0} 帧 · 最近 CAN{1} 收到 {2} 帧", _canReceivedFrameCount, channel, frames.Count);
                    }
                    await Task.Delay(receivedAny ? 1 : 20, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                CanReceiveStatusText.Text = "自动接收：已停止";
            }
            catch (Exception exception)
            {
                CanReceiveStatusText.Text = "自动接收：异常停止 · " + exception.Message;
                CanErrorText.Text = "最近错误：自动接收失败：" + exception.Message;
                await _runtime.Logger.LogAsync(new OperationLogEntry
                {
                    Level = "ERROR",
                    Device = "CAN",
                    Function = "自动接收",
                    ErrorCode = "0x" + exception.HResult.ToString("X8", CultureInfo.InvariantCulture),
                    Message = exception.Message,
                    RawResponse = exception.ToString()
                });
            }
            finally
            {
                if (ReferenceEquals(_canReceiveCts, cts))
                {
                    _canReceiveCts = null;
                    _canReceiveTask = Task.CompletedTask;
                }
                cts.Dispose();
            }
        }

        private async Task StopCanReceiveLoopAsync()
        {
            var cts = _canReceiveCts;
            var task = _canReceiveTask;
            if (cts == null) return;
            cts.Cancel();
            try { await task; } catch (OperationCanceledException) { }
        }

        private void AppendCanFrames(string direction, IEnumerable<CanFrame> frames)
        {
            foreach (var frame in frames) AppendCanFrame(direction, frame, false);
            RenderCanFrames();
        }

        private void AppendCanFrame(string direction, CanFrame frame, bool render = true)
        {
            var timestamp = DateTime.Now;
            _canFrameRecords.Add(new CanFrameRecord(timestamp, direction, frame.ChannelIndex, frame.Id, FormatCanFrameLine(timestamp, direction, frame)));
            while (_canFrameRecords.Count > 10000) _canFrameRecords.RemoveAt(0);
            if (render) RenderCanFrames();
        }

        private void RenderCanFrames()
        {
            if (CanFramesText == null || CanFrameChannelFilterBox == null || CanFrameDirectionFilterBox == null || CanFrameIdFilterText == null) return;
            int? channel = null;
            var channelText = ComboBoxItemText(CanFrameChannelFilterBox);
            if (channelText.StartsWith("CAN", StringComparison.OrdinalIgnoreCase))
                channel = ParseInteger(channelText.Substring(3), "CAN 筛选通道", 0, 63);

            string direction = null;
            var directionText = ComboBoxItemText(CanFrameDirectionFilterBox);
            if (directionText.IndexOf("TX", StringComparison.OrdinalIgnoreCase) >= 0) direction = "TX";
            else if (directionText.IndexOf("RX", StringComparison.OrdinalIgnoreCase) >= 0) direction = "RX";

            uint filterId;
            uint? id = null;
            var idText = (CanFrameIdFilterText.Text ?? string.Empty).Trim();
            if (idText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) idText = idText.Substring(2);
            if (idText.Length > 0)
            {
                if (!uint.TryParse(idText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out filterId) || filterId > 0x1FFFFFFF)
                {
                    CanFramesText.Clear();
                    CanFrameFilterStatusText.Text = "ID 筛选必须是 0～1FFFFFFF 的十六进制数";
                    return;
                }
                id = filterId;
            }

            var matches = _canFrameRecords.Where(record =>
                (!channel.HasValue || record.ChannelIndex == channel.Value) &&
                (direction == null || string.Equals(record.Direction, direction, StringComparison.OrdinalIgnoreCase)) &&
                (!id.HasValue || record.Id == id.Value)).ToList();
            const int maximumVisibleRecords = 3000;
            var visible = matches.Count <= maximumVisibleRecords ? matches : matches.Skip(matches.Count - maximumVisibleRecords).ToList();
            var builder = new StringBuilder();
            foreach (var record in visible) builder.AppendLine(record.DisplayText);
            CanFramesText.Text = builder.ToString();
            CanFramesText.ScrollToEnd();
            CanFrameFilterStatusText.Text = string.Format(CultureInfo.InvariantCulture,
                "记录 {0} 条 · 当前筛选 {1} 条{2}", _canFrameRecords.Count, matches.Count,
                matches.Count > maximumVisibleRecords ? " · 显示最近 " + maximumVisibleRecords + " 条" : string.Empty);
        }

        private static string ComboBoxItemText(ComboBox box)
        {
            var item = box.SelectedItem as ComboBoxItem;
            return item == null ? Convert.ToString(box.SelectedItem, CultureInfo.InvariantCulture) ?? string.Empty : Convert.ToString(item.Content, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static string FormatCanFrameLine(DateTime timestamp, string direction, CanFrame frame)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:HH:mm:ss.fff}  {1,-2}  {2}", timestamp, direction, frame);
        }

        private async void ScopeConnect_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteUiAsync("ZDS2024C Plus", "连接", ScopeResultText, ScopeErrorText, async token =>
            {
                if (!_runtime.Oscilloscope.IsConnected)
                {
                    ApplyScopeConnectionConfigFromUi();
                    await _runtime.Oscilloscope.ConnectAsync(token);
                }
                ScopeStatusText.Text = "已连接";
                return string.Format(CultureInfo.InvariantCulture, "连接成功：{0}:{1}", _runtime.Config.Oscilloscope.Host, _runtime.Config.Oscilloscope.Port);
            });
        }
        private async void ScopeDisconnect_Click(object sender, RoutedEventArgs e) { await DisconnectAsync("ZDS2024C Plus", _runtime.Oscilloscope, ScopeStatusText, ScopeResultText, ScopeErrorText); }

        private async void ScopeApply_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteUiAsync("ZDS2024C Plus", "应用界面参数", ScopeResultText, ScopeErrorText, async token =>
            {
                var config = ReadScopeConfigFromUi();
                await _runtime.Oscilloscope.ApplyConfigAsync(config, token);
                _runtime.Config.Oscilloscope = config;
                var channels = string.Join("、", config.Channels.Where(channel => channel.Enabled).Select(channel => "CH" + channel.Number));
                return string.Format(CultureInfo.InvariantCulture, "已应用时基 {0}、{1} 点、通道 {2}", config.TimeBase, config.SampleDepth, string.IsNullOrEmpty(channels) ? "无" : channels);
            });
        }

        private async void ScopeRun_Click(object sender, RoutedEventArgs e) { await ScopeSimpleAsync("运行", token => _runtime.Oscilloscope.RunAsync(token)); }
        private async void ScopeStop_Click(object sender, RoutedEventArgs e) { await ScopeSimpleAsync("停止", token => _runtime.Oscilloscope.StopAsync(token)); }
        private async void ScopeReset_Click(object sender, RoutedEventArgs e) { await ScopeSimpleAsync("复位", token => _runtime.Oscilloscope.ResetAsync(token)); }

        private Task ScopeSimpleAsync(string function, Func<CancellationToken, Task> action)
        {
            return ExecuteUiAsync("ZDS2024C Plus", function, ScopeResultText, ScopeErrorText, async token => { await action(token); return function + "命令已发送"; });
        }

        private async void ScopeWaveform_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteUiAsync("ZDS2024C Plus", "读取波形", ScopeResultText, ScopeErrorText, async token =>
            {
                var waveform = await _runtime.Oscilloscope.ReadWaveformsAsync(token);
                var path = Path.Combine(EnsureCaptureDirectory(), "ZDS_" + DateTime.Now.ToString("yyyyMMdd_HHmmssfff", CultureInfo.InvariantCulture) + ".wfm");
                await WriteFileAsync(path, waveform.RawWfmBytes, token);
                var preview = await _runtime.Oscilloscope.CaptureBitmapAsync(token);
                DisplayScopeBitmap(preview);
                var summary = waveform.ToString();
                waveform.RawWfmBytes = new byte[0];
                return summary + "；已保存 " + path + "；多通道波形已显示";
            });
        }

        private async void ScopeCapture_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteUiAsync("ZDS2024C Plus", "截图", ScopeResultText, ScopeErrorText, async token =>
            {
                var bitmap = await _runtime.Oscilloscope.CaptureBitmapAsync(token);
                var path = Path.Combine(EnsureCaptureDirectory(), "ZDS_" + DateTime.Now.ToString("yyyyMMdd_HHmmssfff", CultureInfo.InvariantCulture) + ".bmp");
                await WriteFileAsync(path, bitmap, token);
                DisplayScopeBitmap(bitmap);
                return "已保存 " + bitmap.Length + " 字节截图：" + path;
            });
        }

        private async void HvConnect_Click(object sender, RoutedEventArgs e)
        {
            await ConnectAsync("高压电源（人工）", _runtime.HighVoltage, HvStatusText, HvResultText, HvErrorText);
        }

        private async void HvOn_Click(object sender, RoutedEventArgs e) { await RecordHvAsync(true); }
        private async void HvOff_Click(object sender, RoutedEventArgs e) { await RecordHvAsync(false); }

        private Task RecordHvAsync(bool enabled)
        {
            return ExecuteUiAsync("高压电源（人工）", enabled ? "记录人工开启" : "记录人工关闭", HvResultText, HvErrorText, async token =>
            {
                await _runtime.HighVoltage.RecordAsync(enabled, HvNoteText.Text, token);
                return "操作员记录状态=" + (enabled ? "已开启" : "已关闭");
            });
        }

        private async void OnClosing(object sender, CancelEventArgs e)
        {
            if (_allowClose) return;
            e.Cancel = true;
            if (_shutdownInProgress) return;
            _shutdownInProgress = true;
            IsEnabled = false;
            GlobalStatusText.Text = "正在安全关闭输出并释放设备…";
            await StopGpdCycleAsync();
            await StopGpdVoltageRampAsync();
            StopPowerContinuous();
            await StopCanPeriodicSendAsync();
            await StopCanReceiveLoopAsync();
            _lifetime.Cancel();
            using (var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                await _runtime.ShutdownAsync(shutdown.Token);
            }
            _allowClose = true;
            var closeOperation = Dispatcher.BeginInvoke(new Action(Close));
        }

        private void InitializeConnectionEditors()
        {
            var commonBaudRates = new[] { "1200", "2400", "4800", "9600", "19200", "38400", "57600", "115200", "230400", "460800", "921600" };
            var commonPorts = Enumerable.Range(1, 32).Select(number => "COM" + number.ToString(CultureInfo.InvariantCulture)).ToArray();

            PlcPcIpText.Text = _runtime.Config.Plc.PcAddress;
            PlcIpText.Text = _runtime.Config.Plc.PlcAddress;
            PlcPortText.Text = _runtime.Config.Plc.Port.ToString(CultureInfo.InvariantCulture);

            SetScopeChoices(GpdPortText, commonPorts, _runtime.Config.AuxiliaryPower.PortName);
            SetScopeChoices(GpdBaudBox, commonBaudRates, _runtime.Config.AuxiliaryPower.BaudRate.ToString(CultureInfo.InvariantCulture));

            SetScopeChoices(DmmPortText, commonPorts, _runtime.Config.Multimeter.PortName);
            SetScopeChoices(DmmBaudBox, commonBaudRates, _runtime.Config.Multimeter.BaudRate.ToString(CultureInfo.InvariantCulture));

            SetScopeChoices(PowerPortText, commonPorts, _runtime.Config.PowerMeter.PortName);
            SetScopeChoices(PowerBaudBox, commonBaudRates, _runtime.Config.PowerMeter.BaudRate.ToString(CultureInfo.InvariantCulture));
            PowerSourceChannelBox.ItemsSource = Enumerable.Range(1, 3).ToArray();
            PowerOutputChannelBox.ItemsSource = Enumerable.Range(1, 3).ToArray();
            PowerSourceChannelBox.SelectedItem = _runtime.Config.PowerMeter.SourceChannel;
            PowerOutputChannelBox.SelectedItem = _runtime.Config.PowerMeter.OutputChannel;

            var load = _runtime.Config.ElectronicLoad;
            LoadIpText.Text = load.Host;
            LoadPortText.Text = load.Port.ToString(CultureInfo.InvariantCulture);
            LoadDeviceIdText.Text = load.DeviceId.ToString(CultureInfo.InvariantCulture);
            InitializeLoadRangeBox(LoadCcRangeBox, load.CcRange);
            InitializeLoadRangeBox(LoadCvRangeBox, load.CvRange);
            InitializeLoadRangeBox(LoadCrRangeBox, load.CrRange);
            InitializeLoadRangeBox(LoadCpRangeBox, load.CpRange);

            ScopeIpText.Text = _runtime.Config.Oscilloscope.Host;
            ScopePortText.Text = _runtime.Config.Oscilloscope.Port.ToString(CultureInfo.InvariantCulture);

            SelectComboBoxItem(CanAdapterModelBox, _runtime.Config.Can.AdapterModel.IndexOf("100U", StringComparison.OrdinalIgnoreCase) >= 0
                ? "ZLG CANFD 100U" : "ZLG CANFD 200U");
            CanDeviceIndexText.Text = _runtime.Config.Can.DeviceIndex.ToString(CultureInfo.InvariantCulture);
            SelectComboBoxItem(CanBusModeBox, _runtime.Config.Can.UseClassicCan ? "CAN" : "CAN FD");
            SetScopeChoices(CanArbitrationBitRateBox,
                new[] { "50", "100", "125", "250", "500", "800", "1000" },
                (_runtime.Config.Can.ArbitrationBitRate / 1000).ToString(CultureInfo.InvariantCulture));
            SetScopeChoices(CanDataBitRateBox,
                new[] { "100", "125", "250", "500", "800", "1000", "2000", "4000", "5000" },
                (_runtime.Config.Can.DataBitRate / 1000).ToString(CultureInfo.InvariantCulture));
            CanTerminationCheck.IsChecked = _runtime.Config.Can.TerminationEnabled;
            RefreshCanChannelChoices();
            CanChannelBox.SelectedItem = Math.Min(_runtime.Config.Can.ChannelIndex, _runtime.Config.Can.ChannelCount - 1);
            RefreshCanFrameTypeUi();
            ApplyCanConfigFromUi();
        }

        private static void ApplySerialConfigFromUi(SerialDeviceConfig config, ComboBox portText, ComboBox baudBox, string deviceName)
        {
            var portName = ComboBoxText(portText).ToUpperInvariant();
            if (!Regex.IsMatch(portName, "^COM[1-9][0-9]*$", RegexOptions.CultureInvariant))
                throw new FormatException(deviceName + " 串口必须采用 COM1、COM12 这样的格式。");
            config.PortName = portName;
            config.BaudRate = ParseInteger(ComboBoxText(baudBox), deviceName + " 波特率", 300, 4000000);
        }

        private void ApplyPowerConfigFromUi(bool includeConnection)
        {
            var config = _runtime.Config.PowerMeter;
            if (includeConnection)
            {
                var portName = ComboBoxText(PowerPortText).ToUpperInvariant();
                if (!Regex.IsMatch(portName, "^COM[1-9][0-9]*$", RegexOptions.CultureInvariant))
                    throw new FormatException("PA333H 串口必须采用 COM1、COM12 这样的格式。");
                config.PortName = portName;
                config.BaudRate = ParseInteger(ComboBoxText(PowerBaudBox), "PA333H 波特率", 300, 4000000);
            }
            config.SourceChannel = SelectedNumber(PowerSourceChannelBox);
            config.OutputChannel = SelectedNumber(PowerOutputChannelBox);
            if (config.SourceChannel == config.OutputChannel) throw new InvalidOperationException("PA333H 源侧通道和输出侧通道不能相同。");
        }

        private void ApplyPlcConnectionConfigFromUi()
        {
            var config = _runtime.Config.Plc;
            config.PcAddress = ParseIpv4(PlcPcIpText.Text, "本机 IP");
            config.PlcAddress = ParseIpv4(PlcIpText.Text, "PLC IP");
            config.Port = ParseInteger(PlcPortText.Text, "PLC 端口", 1, 65535);
        }

        private void ApplyLoadConfigFromUi(bool includeConnection)
        {
            var config = _runtime.Config.ElectronicLoad;
            if (includeConnection)
            {
                config.Host = ParseIpv4(LoadIpText.Text, "N69206 IP");
                config.Port = ParseInteger(LoadPortText.Text, "N69206 端口", 1, 65535);
                config.DeviceId = ParseInteger(LoadDeviceIdText.Text, "N69206 设备 ID", 1, 248);
            }
            ApplyLoadRangesFromUi(true);
        }

        private void ApplyLoadRangesFromUi(bool requireAll)
        {
            if (LoadCcRangeBox.SelectedItem == null || LoadCvRangeBox.SelectedItem == null ||
                LoadCrRangeBox.SelectedItem == null || LoadCpRangeBox.SelectedItem == null)
            {
                if (requireAll) throw new InvalidOperationException("请完整选择 N69206 的 CC、CV、CR、CP 量程。");
                return;
            }
            var config = _runtime.Config.ElectronicLoad;
            config.CcRange = SelectedLoadRange(LoadCcRangeBox);
            config.CvRange = SelectedLoadRange(LoadCvRangeBox);
            config.CrRange = SelectedLoadRange(LoadCrRangeBox);
            config.CpRange = SelectedLoadRange(LoadCpRangeBox);
        }

        private void ApplyScopeConnectionConfigFromUi()
        {
            var config = _runtime.Config.Oscilloscope;
            config.Host = ParseIpv4(ScopeIpText.Text, "示波器 IP");
            config.Port = ParseInteger(ScopePortText.Text, "示波器端口", 1, 65535);
        }

        private static string ParseIpv4(string text, string name)
        {
            IPAddress address;
            var value = (text ?? string.Empty).Trim();
            if (!IPAddress.TryParse(value, out address) || address.AddressFamily != AddressFamily.InterNetwork)
                throw new FormatException(name + "必须是有效的 IPv4 地址，例如 192.168.4.101。");
            return address.ToString();
        }

        private static void InitializeLoadRangeBox(ComboBox box, int selectedCode)
        {
            var choices = new[]
            {
                new LoadRangeChoice(0, "大量程 (0)"),
                new LoadRangeChoice(1, "小量程 (1)"),
                new LoadRangeChoice(2, "中量程 (2)")
            };
            box.ItemsSource = choices;
            box.SelectedItem = choices.First(choice => choice.Code == selectedCode);
        }

        private static int SelectedLoadRange(ComboBox box)
        {
            var choice = box.SelectedItem as LoadRangeChoice;
            if (choice == null) throw new InvalidOperationException("尚未选择 N69206 量程。");
            return choice.Code;
        }

        private static string ComboBoxText(ComboBox box)
        {
            var item = box.SelectedItem as ComboBoxItem;
            var value = box.IsEditable ? box.Text : item == null ? Convert.ToString(box.SelectedItem, CultureInfo.InvariantCulture) : Convert.ToString(item.Content, CultureInfo.InvariantCulture);
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0) throw new FormatException("界面配置项不能为空。");
            return value;
        }

        private static int ParseInteger(string text, string name, int minimum, int maximum)
        {
            int value;
            if (!int.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < minimum || value > maximum)
                throw new ArgumentOutOfRangeException(name, name + "必须为 " + minimum.ToString(CultureInfo.InvariantCulture) + "～" + maximum.ToString(CultureInfo.InvariantCulture) + " 的整数。");
            return value;
        }

        private static void SelectComboBoxItem(ComboBox box, string content)
        {
            foreach (var entry in box.Items)
            {
                var item = entry as ComboBoxItem;
                if (item != null && string.Equals(Convert.ToString(item.Content, CultureInfo.InvariantCulture), content, StringComparison.OrdinalIgnoreCase))
                {
                    box.SelectedItem = item;
                    return;
                }
            }
            throw new InvalidOperationException("界面选项不存在：" + content);
        }

        private static int SelectedNumber(ComboBox box)
        {
            var item = box.SelectedItem as ComboBoxItem;
            if (box.SelectedItem == null) throw new InvalidOperationException("尚未选择数值项。");
            return int.Parse(item == null ? box.SelectedItem.ToString() : item.Content.ToString(), CultureInfo.InvariantCulture);
        }

        private static decimal ParseDecimal(string text, string name)
        {
            decimal value;
            if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return value;
            if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)) return value;
            throw new FormatException(name + "不是有效数字。");
        }

        private static int ParseMilliseconds(string text, string name)
        {
            int value;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
                !int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value))
            {
                throw new FormatException(name + "不是有效的毫秒整数。");
            }
            if (value < 1 || value > 86400000) throw new ArgumentOutOfRangeException(name, name + "必须为 1～86400000 ms。");
            return value;
        }

        private void RefreshLoadModeUi()
        {
            if (LoadModeBox.SelectedItem == null) return;
            var mode = (ElectronicLoadMode)LoadModeBox.SelectedItem;
            var config = _runtime.Config.ElectronicLoad;
            LoadValueLabel.Text = mode == ElectronicLoadMode.ConstantCurrent ? "Current" : mode == ElectronicLoadMode.ConstantVoltage ? "Voltage" : mode == ElectronicLoadMode.ConstantResistance ? "Resistance" : "Power";
            LoadValueUnitText.Text = mode == ElectronicLoadMode.ConstantCurrent ? "A" : mode == ElectronicLoadMode.ConstantVoltage ? "V" : mode == ElectronicLoadMode.ConstantResistance ? "Ω" : "W";
            var rangeBox = mode == ElectronicLoadMode.ConstantCurrent ? LoadCcRangeBox
                : mode == ElectronicLoadMode.ConstantVoltage ? LoadCvRangeBox
                : mode == ElectronicLoadMode.ConstantResistance ? LoadCrRangeBox
                : LoadCpRangeBox;
            var range = rangeBox.SelectedItem == null ? LoadRange(config, mode) : SelectedLoadRange(rangeBox);
            LoadRangeText.Text = LoadRangeName(range);
            var voltage = mode == ElectronicLoadMode.ConstantVoltage;
            LoadSlopePanel.Visibility = voltage ? Visibility.Collapsed : Visibility.Visible;
            LoadVoltageRatePanel.Visibility = voltage ? Visibility.Visible : Visibility.Collapsed;
        }

        private static int LoadRange(ElectronicLoadConfig config, ElectronicLoadMode mode)
        {
            if (mode == ElectronicLoadMode.ConstantCurrent) return config.CcRange;
            if (mode == ElectronicLoadMode.ConstantVoltage) return config.CvRange;
            if (mode == ElectronicLoadMode.ConstantResistance) return config.CrRange;
            return config.CpRange;
        }

        private static string LoadRangeName(int range)
        {
            return range == 0 ? "大量程 (0)" : range == 1 ? "小量程 (1)" : "中量程 (2)";
        }

        private static string LoadModeName(ElectronicLoadMode mode)
        {
            return mode == ElectronicLoadMode.ConstantCurrent ? "CC" : mode == ElectronicLoadMode.ConstantVoltage ? "CV" : mode == ElectronicLoadMode.ConstantResistance ? "CR" : "CP";
        }

        private void RefreshDmmRanges()
        {
            if (DmmFunctionBox.SelectedItem == null) return;
            DmmRangeBox.ItemsSource = DmmRanges((MultimeterFunction)DmmFunctionBox.SelectedItem);
            DmmRangeBox.SelectedIndex = 0;
        }

        private static IReadOnlyList<DmmRangeChoice> DmmRanges(MultimeterFunction function)
        {
            if (function == MultimeterFunction.DcVoltage)
                return Choices("自动量程", new[] { "100 mV", "1 V", "10 V", "100 V", "1000 V" }, new decimal[] { 0.1m, 1m, 10m, 100m, 1000m });
            if (function == MultimeterFunction.AcVoltage)
                return Choices("自动量程", new[] { "100 mV", "1 V", "10 V", "100 V", "750 V" }, new decimal[] { 0.1m, 1m, 10m, 100m, 750m });
            if (function == MultimeterFunction.DcCurrent || function == MultimeterFunction.AcCurrent)
                return Choices("自动量程", new[] { "100 µA", "1 mA", "10 mA", "100 mA", "1 A", "3 A", "10 A" }, new decimal[] { 0.0001m, 0.001m, 0.01m, 0.1m, 1m, 3m, 10m });
            if (function == MultimeterFunction.Resistance2Wire || function == MultimeterFunction.Resistance4Wire)
                return Choices("自动量程", new[] { "100 Ω", "1 kΩ", "10 kΩ", "100 kΩ", "1 MΩ", "10 MΩ", "100 MΩ" }, new decimal[] { 100m, 1000m, 10000m, 100000m, 1000000m, 10000000m, 100000000m });
            if (function == MultimeterFunction.Diode) return new[] { new DmmRangeChoice("固定 5 V", null) };
            if (function == MultimeterFunction.Continuity) return new[] { new DmmRangeChoice("固定 1 kΩ", null) };
            return new[] { new DmmRangeChoice("自动量程", null) };
        }

        private static IReadOnlyList<DmmRangeChoice> Choices(string automaticLabel, string[] labels, decimal[] values)
        {
            var result = new List<DmmRangeChoice> { new DmmRangeChoice(automaticLabel, null) };
            for (var index = 0; index < labels.Length; index++) result.Add(new DmmRangeChoice(labels[index], values[index]));
            return result;
        }

        private void RefreshOpenCanChannels()
        {
            var channels = _runtime.DcdcCan.OpenChannels;
            CanOpenChannelsText.Text = channels.Count == 0 ? "无" : string.Join("、", channels.Select(channel => "CAN" + channel));
        }

        private void RefreshCanConnectionState()
        {
            RefreshOpenCanChannels();
            var connected = _runtime.DcdcCan.IsConnected;
            SetCanConnectionSettingsEnabled(!connected);
            if (!connected)
            {
                if (_canReceiveCts != null) _canReceiveCts.Cancel();
                CanReceiveStatusText.Text = "自动接收：等待通道打开";
                CanStatusText.Text = "未连接";
                return;
            }
            var channels = _runtime.DcdcCan.OpenChannels;
            CanStatusText.Text = channels.Count == 0
                ? "适配器已连接，通道未打开"
                : "适配器已连接 · 已打开 " + string.Join("、", channels.Select(value => "CAN" + value));
            if (channels.Count == 0)
            {
                if (_canReceiveCts != null) _canReceiveCts.Cancel();
                CanReceiveStatusText.Text = "自动接收：等待通道打开";
            }
            else
            {
                EnsureCanReceiveLoop();
            }
        }

        private void InitializeScopeUi()
        {
            var config = _runtime.Config.Oscilloscope;
            SetScopeChoices(ScopeTimeBaseBox, new[]
            {
                "1ns", "2ns", "5ns", "10ns", "25ns", "50ns", "100ns", "200ns", "500ns",
                "1us", "2us", "5us", "10us", "20us", "50us", "100us", "200us", "500us",
                "1ms", "2ms", "5ms", "10ms", "20ms", "50ms", "100ms", "200ms", "500ms",
                "1s", "2s", "5s", "10s", "20s", "50s", "100s", "200s", "500s", "1000s"
            }, config.TimeBase);
            SetScopeChoices(ScopeTimeBaseModeBox, new[] { "Main", "XY", "Roll" }, config.TimeBaseMode);
            SetScopeChoices(ScopeAcquisitionModeBox, new[] { "Normal", "Peak", "Averages", "HResolution" }, config.AcquisitionMode);
            SetScopeChoices(ScopeAverageCountBox, new[] { "2", "4", "8", "16", "32", "64", "128", "256", "512", "1024" }, config.AverageCount.ToString(CultureInfo.InvariantCulture));
            ScopeHorizontalOffsetText.Text = config.HorizontalOffset;
            ScopeRollingCheck.IsChecked = config.RollingMode;
            ScopeSampleDepthText.Text = config.SampleDepth.ToString(CultureInfo.InvariantCulture);

            foreach (var editor in ScopeChannelEditors())
            {
                var channel = config.Channels.First(item => item.Number == editor.Number);
                editor.Enabled.IsChecked = channel.Enabled;
                SetScopeChoices(editor.Coupling, new[] { "DC", "AC", "GND" }, channel.Coupling);
                SetScopeChoices(editor.Bandwidth, new[] { "Off", "20M" }, channel.BandwidthLimit);
                SetScopeChoices(editor.Unit, new[] { "Voltage", "Ampere" }, channel.Unit);
                SetScopeChoices(editor.Probe, new[] { "0.001", "0.002", "0.005", "0.01", "0.02", "0.05", "0.1", "0.2", "0.5", "1", "2", "5", "10", "20", "50", "100", "200", "500", "1000" }, channel.ProbeRatio.ToString("0.###", CultureInfo.InvariantCulture));
                SetScopeChoices(editor.Scale, new[] { "0.002", "0.005", "0.01", "0.02", "0.05", "0.1", "0.2", "0.5", "1", "2", "5", "10" }, channel.VerticalScale.ToString("0.###", CultureInfo.InvariantCulture));
                editor.Offset.Text = channel.VerticalOffset.ToString("0.###", CultureInfo.InvariantCulture);
                editor.Inverted.IsChecked = channel.Inverted;
            }
        }

        private OscilloscopeConfig ReadScopeConfigFromUi()
        {
            var source = _runtime.Config.Oscilloscope;
            int averageCount;
            int sampleDepth;
            if (!int.TryParse(ScopeChoice(ScopeAverageCountBox), NumberStyles.Integer, CultureInfo.InvariantCulture, out averageCount) || averageCount < 1 || averageCount > 65536)
                throw new FormatException("平均次数必须是 1～65536 的整数。");
            if (!int.TryParse(ScopeSampleDepthText.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out sampleDepth))
                throw new FormatException("存储深度不是有效整数。");

            var config = new OscilloscopeConfig
            {
                Model = source.Model,
                Host = ParseIpv4(ScopeIpText.Text, "示波器 IP"),
                Port = ParseInteger(ScopePortText.Text, "示波器端口", 1, 65535),
                TimeoutMilliseconds = source.TimeoutMilliseconds,
                HorizontalOffset = ScopeHorizontalOffsetText.Text.Trim(),
                TimeBase = ScopeChoice(ScopeTimeBaseBox),
                TimeBaseMode = ScopeChoice(ScopeTimeBaseModeBox),
                AverageCount = averageCount,
                AcquisitionMode = ScopeChoice(ScopeAcquisitionModeBox),
                SampleDepth = sampleDepth,
                RollingMode = ScopeRollingCheck.IsChecked == true
            };

            foreach (var editor in ScopeChannelEditors())
            {
                config.Channels.Add(new OscilloscopeChannelConfig
                {
                    Number = editor.Number,
                    Name = "Channel " + editor.Number.ToString(CultureInfo.InvariantCulture),
                    Enabled = editor.Enabled.IsChecked == true,
                    BandwidthLimit = ScopeChoice(editor.Bandwidth),
                    Coupling = ScopeChoice(editor.Coupling),
                    Inverted = editor.Inverted.IsChecked == true,
                    ProbeRatio = ParseDecimal(ScopeChoice(editor.Probe), "CH" + editor.Number + " 探头倍率"),
                    Unit = ScopeChoice(editor.Unit),
                    VerticalScale = ParseDecimal(ScopeChoice(editor.Scale), "CH" + editor.Number + " 垂直档位"),
                    VerticalOffset = ParseDecimal(editor.Offset.Text, "CH" + editor.Number + " 垂直偏置")
                });
            }
            if (!config.Channels.Any(channel => channel.Enabled)) throw new InvalidOperationException("至少启用一个示波器通道。");
            return config;
        }

        private IEnumerable<ScopeChannelEditor> ScopeChannelEditors()
        {
            yield return new ScopeChannelEditor(1, ScopeCh1Enabled, ScopeCh1Coupling, ScopeCh1Bandwidth, ScopeCh1Unit, ScopeCh1Probe, ScopeCh1Scale, ScopeCh1Offset, ScopeCh1Invert);
            yield return new ScopeChannelEditor(2, ScopeCh2Enabled, ScopeCh2Coupling, ScopeCh2Bandwidth, ScopeCh2Unit, ScopeCh2Probe, ScopeCh2Scale, ScopeCh2Offset, ScopeCh2Invert);
            yield return new ScopeChannelEditor(3, ScopeCh3Enabled, ScopeCh3Coupling, ScopeCh3Bandwidth, ScopeCh3Unit, ScopeCh3Probe, ScopeCh3Scale, ScopeCh3Offset, ScopeCh3Invert);
            yield return new ScopeChannelEditor(4, ScopeCh4Enabled, ScopeCh4Coupling, ScopeCh4Bandwidth, ScopeCh4Unit, ScopeCh4Probe, ScopeCh4Scale, ScopeCh4Offset, ScopeCh4Invert);
        }

        private static void SetScopeChoices(ComboBox box, IEnumerable<string> choices, string selected)
        {
            var values = choices.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var actual = values.FirstOrDefault(value => string.Equals(value, selected, StringComparison.OrdinalIgnoreCase));
            if (actual == null)
            {
                actual = selected;
                values.Insert(0, selected);
            }
            box.ItemsSource = values;
            box.SelectedItem = actual;
            if (box.IsEditable) box.Text = actual;
        }

        private static string ScopeChoice(ComboBox box)
        {
            var value = box.IsEditable ? box.Text : box.SelectedItem == null ? string.Empty : box.SelectedItem.ToString();
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0) throw new FormatException("示波器参数不能为空。");
            return value;
        }

        private static string EnsureCaptureDirectory()
        {
            var directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "Captures");
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static async Task WriteFileAsync(string path, byte[] bytes, CancellationToken cancellationToken)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 81920, true))
            {
                await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
            }
        }

        private void DisplayScopeBitmap(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes, false))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                ScopeWaveformImage.Source = image;
            }
        }

        private sealed class ScopeChannelEditor
        {
            public ScopeChannelEditor(int number, CheckBox enabled, ComboBox coupling, ComboBox bandwidth, ComboBox unit, ComboBox probe, ComboBox scale, TextBox offset, CheckBox inverted)
            {
                Number = number;
                Enabled = enabled;
                Coupling = coupling;
                Bandwidth = bandwidth;
                Unit = unit;
                Probe = probe;
                Scale = scale;
                Offset = offset;
                Inverted = inverted;
            }

            public int Number { get; }
            public CheckBox Enabled { get; }
            public ComboBox Coupling { get; }
            public ComboBox Bandwidth { get; }
            public ComboBox Unit { get; }
            public ComboBox Probe { get; }
            public ComboBox Scale { get; }
            public TextBox Offset { get; }
            public CheckBox Inverted { get; }
        }

        private sealed class DmmRangeChoice
        {
            public DmmRangeChoice(string label, decimal? value)
            {
                Label = label;
                Value = value;
            }

            public string Label { get; }
            public decimal? Value { get; }
            public override string ToString() { return Label; }
        }

        private sealed class LoadRangeChoice
        {
            public LoadRangeChoice(int code, string label)
            {
                Code = code;
                Label = label;
            }

            public int Code { get; }
            public string Label { get; }
            public override string ToString() { return Label; }
        }

        private sealed class CanFrameRecord
        {
            public CanFrameRecord(DateTime timestamp, string direction, int channelIndex, uint id, string displayText)
            {
                Timestamp = timestamp;
                Direction = direction;
                ChannelIndex = channelIndex;
                Id = id;
                DisplayText = displayText;
            }

            public DateTime Timestamp { get; }
            public string Direction { get; }
            public int ChannelIndex { get; }
            public uint Id { get; }
            public string DisplayText { get; }
        }
    }
}
