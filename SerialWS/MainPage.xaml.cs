// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.ObjectModel;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.Devices.Enumeration;
using Windows.Devices.SerialCommunication;
using Windows.Storage.Streams;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using Windows.Storage;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Collections.Generic;

using PacketManagement;
using System.Text.RegularExpressions;
using SerialWS.Exceptions;
using Windows.UI.Xaml.Media;
using Windows.UI.Text;
using System.Text;
using System.IO;
using Windows.Networking.Sockets;
using Windows.System.Threading;

namespace SerialWS
{

    public class HostViewModel : INotifyPropertyChanged
    {
        private string selectedFileName;
        private string status;
        private int rxOrTxOption;
        private ObservableCollection<Command> listOfCommands;

        public event PropertyChangedEventHandler PropertyChanged = delegate { };

        private string _textToSend;

        public string TextToSend {
            get { return _textToSend; }
            set { _textToSend = value;
                this.OnPropertyChanged("TextToSend");
            }
        }

        public HostViewModel()
        {
            this.selectedFileName = "Please open a file";
            this.status = "Select a device and connect";
        }

        public ObservableCollection<Command> ListOfCommands {
            get { return this.listOfCommands; }
            set {
                this.listOfCommands = value;
                this.OnPropertyChanged("ListOfCommands");
            }
        }

        public string SelectedFileName {
            get { return this.selectedFileName; }
            set {
                this.selectedFileName = value;
                this.OnPropertyChanged("SelectedFileName");
            }
        }

        public int RxOrTxOption {
            get { return this.rxOrTxOption; }
            set {
                this.rxOrTxOption = value;
                this.OnPropertyChanged("RxOrTxOption");
            }
        }




        public string StatusString {
            get { return this.status; }
            set {
                this.status = value;
                this.OnPropertyChanged("StatusString");
            }
        }


        public void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            // Raise the PropertyChanged event, passing the name of the property whose value has changed.
            this.PropertyChanged(this, new PropertyChangedEventArgs(propertyName));
        }
    }


    public sealed partial class MainPage : Page, IAckCallback
    {
        /// <summary>
        /// Private variables
        /// </summary>
        private SerialDevice serialPort = null;
        DataWriter dataWriteObject = null;
        DataReader dataReaderObject = null;
        static Array baudRate = new uint[] { 4800, 7200, 9600, 19200, 28800, 38400, 56000, 57600, 76800, 115200 };

        private StreamSocket clientSocket = null;


        public HostViewModel ViewModel { get; set; } = new HostViewModel();


        private ObservableCollection<DeviceInformation> listOfDevices;
        
        private CancellationTokenSource ReadCancellationTokenSource;

        private PacketManager pMan;


        private StorageFile SelectedFile;
        private ulong readingIndex;

        ApplicationDataContainer localSettings;

        private byte[] lastReceivedBytes;

        public MainPage() {
            pMan = new PacketManager(new List<Tuple<ushort, string>>());
            this.InitializeComponent();
            this.DataContext = this;
            pMan.callback = this;
            ViewModel.TextToSend = "";
            oldText = "";

            comPortInput.IsEnabled = false;

            restoreSettings();

            ListAvailablePorts();
        }

        public void OnAck() {
            if (SelectedFile != null) {
                SendFileSection(); 
            }
        }

        public void OnNack(string msg) {
            if (SelectedFile != null) {
                readingIndex = 0;
                SelectedFile = null;
                status.Text = "Expected Ack, received " + msg;
            }
           if (dataWriteObject != null)
            {
                dataWriteObject.DetachStream();
                dataWriteObject = null;
            }
        }


        private async void restoreSettings() {
           localSettings =ApplicationData.Current.LocalSettings;
           StorageFolder localFolder = ApplicationData.Current.LocalFolder;
           ApplicationDataCompositeValue composite = (ApplicationDataCompositeValue)localSettings.Values[Constants.SETTINGSKEY];

            if (composite == null) {
                composite = new ApplicationDataCompositeValue();
                composite[Constants.COMMANDCHOICEKEY] = 0;
                composite[Constants.SENDERKEY] = "00";
                composite[Constants.RECEIVERKEY] = "00";
                composite[Constants.BAUDKEY] = 0;
                composite[Constants.IPADDR] = "";
                composite[Constants.PORT] = "80";
            }

            List<Tuple<UInt16, string>> commands = new List<Tuple<ushort, string>>();
            try {
                StorageFile file = await localFolder.GetFileAsync("config.csv");
                string text = await FileIO.ReadTextAsync(file);
                commands = Utils.readCSV(text);
                SetCommandList(commands);
                //tempsavecomm();
            }
            catch (Exception ex1) {
                SetCommandList(new List<Tuple<ushort, string>>());
                status.Text = ex1.ToString();
            }

            baudRateSource.Source = baudRate;
            baudCombox.SelectedIndex = (int)composite[Constants.BAUDKEY];
            senderAdd.Text = (string)composite[Constants.SENDERKEY];
            receiverAdd.Text = (string)composite[Constants.RECEIVERKEY];
            ipAddrTextBox.Text = (string)composite[Constants.IPADDR];
            portTextBox.Text = (string)composite[Constants.PORT];

            localSettings.Values[Constants.SETTINGSKEY] = composite;
        }

        /// <summary>
        /// ListAvailablePorts
        /// - Use SerialDevice.GetDeviceSelector to enumerate all serial devices
        /// - Attaches the DeviceInformation to the ListBox source so that DeviceIds are displayed
        /// </summary>
        private async void ListAvailablePorts() {
            listOfDevices = new ObservableCollection<DeviceInformation>();
            try {
                string aqs = SerialDevice.GetDeviceSelector();
                var dis = await DeviceInformation.FindAllAsync(aqs);

                status.Text = "Select a device and connect";

                for (int i = 0; i < dis.Count; i++) {
                    listOfDevices.Add(dis[i]);
                }

                DeviceListSource.Source = listOfDevices;
                comPortInput.IsEnabled = true;
                ConnectDevices.SelectedIndex = -1;
            } catch (Exception ex1) {
                status.Text = ex1.Message;
            }
        }

        /// <summary>
        /// comPortInput_Click: Action to take when 'Connect' button is clicked
        /// - Get the selected device index and use Id to create the SerialDevice object
        /// - Configure default settings for the serial port
        /// - Create the ReadCancellationTokenSource token
        /// - Start listening on the serial port input
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void comPortInput_Click(object sender, RoutedEventArgs e) {
            var selection = ConnectDevices.SelectedItems;

            if (selection.Count <= 0) {
                status.Text = "Select a device and connect";
                return;
            }

            DeviceInformation entry = (DeviceInformation)selection[0];

            try {
                serialPort = await SerialDevice.FromIdAsync(entry.Id);

                // Disable the 'Connect' button 
                comPortInput.IsEnabled = false;

                // Configure serial settings
                serialPort.WriteTimeout = TimeSpan.FromMilliseconds(1000);
                serialPort.ReadTimeout = TimeSpan.FromMilliseconds(1000);
                serialPort.BaudRate = (uint)baudCombox.SelectedItem;
                serialPort.Parity = SerialParity.None;
                serialPort.StopBits = SerialStopBitCount.One;
                serialPort.DataBits = 8;
                serialPort.Handshake = SerialHandshake.None;

                // Display configured settings
                status.Text = "Serial port configured successfully: ";
                status.Text += serialPort.BaudRate + "-";
                status.Text += serialPort.DataBits + "-";
                status.Text += serialPort.Parity.ToString() + "-";
                status.Text += serialPort.StopBits;

                // Set the RcvdText field to invoke the TextChanged callback
                // The callback launches an async Read task to wait for data
                rcvdText.Text = "Waiting for data...";

                // Create cancellation token object to close I/O operations when closing the device
                ReadCancellationTokenSource = new CancellationTokenSource();

                // Enable 'WRITE' button to allow sending data
                ConnectDeviceFlyout.Hide();
                baudCombox.IsEnabled = false;
                ListenSerial();
            } catch (Exception ex1) {
                status.Text = ex1.Message;
                comPortInput.IsEnabled = true;
            }
        }

        
        /// <summary>
        /// WriteAsync: Task that asynchronously writes data from the input text box 'sendText' to the OutputStream 
        /// </summary>
        /// <returns></returns>
        private async Task WriteAsync(byte[] text) {
            Task<UInt32> storeAsyncTask;
            byte a, b;
            byte[] c = new byte[2];

            try {
                b = (byte)int.Parse(senderAdd.Text, System.Globalization.NumberStyles.HexNumber);
                a = (byte)int.Parse(receiverAdd.Text, System.Globalization.NumberStyles.HexNumber);
                c[0] = (byte)int.Parse(CommandCode.Text, System.Globalization.NumberStyles.HexNumber);
                c[1] = (byte)int.Parse(SubCommandCode.Text, System.Globalization.NumberStyles.HexNumber);
            } catch (FormatException) {
                status.Text = "Wrong sender/receiver address or command code";
                return;
            }
            List<byte[]> toSend = Utils.formPackets(text, b, a, c);

            foreach (byte[] packet in toSend) {
                // Load the text from the sendText input text box to the dataWriter object
                //dataWriteObject.WriteString(text);
                dataWriteObject.WriteBytes(packet);

                // Launch an async task to complete the write operation
                storeAsyncTask = dataWriteObject.StoreAsync().AsTask();

                UInt32 bytesWritten = await storeAsyncTask;
                if (bytesWritten > 0) {
                    status.Text = bytesWritten + " bytes written successfully!";

                    if (ViewModel.RxOrTxOption == 1) {
                        rcvdText.Text = BitConverter.ToString(packet);
                    } else {
                        rcvdText.Text = "";
                    }
                }
            }
        }



        private async void ListenSock(string dest, string port) {
            try {
                //Create the StreamSocket and establish a connection to the echo server.
                clientSocket = new StreamSocket();

                //The server hostname that we will be establishing a connection to. We will be running the server and client locally,
                //so we will use localhost as the hostname.
                Windows.Networking.HostName serverHost = new Windows.Networking.HostName(dest);

                //Every protocol typically has a standard port number. For example HTTP is typically 80, FTP is 20 and 21, etc.
                //For the echo server/client application we will use a random port 1337.
                await clientSocket.ConnectAsync(serverHost, port);

                status.Text = "Socket succesfully connected!";

                dataReaderObject = new DataReader(clientSocket.InputStream);
//                ReadCancellationTokenSource.CancelAfter(2000);

                pMan.TIMEOUT = 1000;

                while (true) {
                    await ReadAsync(ReadCancellationTokenSource.Token);
                }
            } catch (Exception ex1) {
                status.Text = ex1.Message;
                CloseDevice();
            }
        }

        
        /// <summary>
        /// - Create a DataReader object
        /// - Create an async task to read from the SerialDevice InputStream
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void ListenSerial() {
            try {
                if (serialPort != null) {
                    dataReaderObject = new DataReader(serialPort.InputStream);

                    pMan.TIMEOUT = 1000;
                    // keep reading the serial input
                    while (true) {
                        await ReadAsync(ReadCancellationTokenSource.Token);
                    }
                }
            } catch (Exception ex1) {
                if (ex1.GetType().Name == "TaskCanceledException") {
                    status.Text = "Reading task was cancelled, closing device and cleaning up";
                    CloseDevice();
                } else {
                    status.Text = ex1.Message;
                }
            } finally {
                // Cleanup once complete
                if (dataReaderObject != null) {
                    dataReaderObject.DetachStream();
                    dataReaderObject = null;
                }
            }
        }


        private ThreadPoolTimer checkTimeout = null;

        /// <summary>
        /// ReadAsync: Task that waits on data and reads asynchronously from the serial device InputStream
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task ReadAsync(CancellationToken cancellationToken) {
            Task<UInt32> loadAsyncTask;
            uint ReadBufferLengthInitial = 4096;
            byte[] initialBuf = new byte[ReadBufferLengthInitial];
            UInt32 bytesRead;
            string firstRes;
            firstRes = "";

            // If task cancellation was requested, comply
            cancellationToken.ThrowIfCancellationRequested();
            dataReaderObject.InputStreamOptions = InputStreamOptions.Partial;

            // Create a task object to wait for data on the serialPort.InputStream
            loadAsyncTask = dataReaderObject.LoadAsync(ReadBufferLengthInitial).AsTask(cancellationToken);

            bytesRead = await loadAsyncTask;

            lastReceivedBytes = new byte[bytesRead];
            dataReaderObject.ReadBytes(lastReceivedBytes);

            firstRes = BitConverter.ToString(lastReceivedBytes);

            pMan.evalNewData(lastReceivedBytes);
            uint remaining = pMan.remaining();

            if (ViewModel.RxOrTxOption == 0) {
                rcvdText.Text = firstRes;
            } 
            status.Text = (bytesRead) + " bytes read successfully!";
        }

        /// <summary>
        /// CancelReadTask:
        /// - Uses the ReadCancellationTokenSource to cancel read operations
        /// </summary>
        private void CancelReadTask() {
            if (ReadCancellationTokenSource != null) {
                if (!ReadCancellationTokenSource.IsCancellationRequested) {
                    ReadCancellationTokenSource.Cancel();
                }
            }
        }

        /// <summary>
        /// CloseDevice:
        /// - Disposes SerialDevice object
        /// - Clears the enumerated device Id list
        /// </summary>
        private void CloseDevice() {
            if (serialPort != null) {
                serialPort.Dispose();
                serialPort = null;
            } else if (clientSocket != null) {
                clientSocket.Dispose();
                clientSocket = null;
            }

            comPortInput.IsEnabled = true;
            rcvdText.Text = "";
            listOfDevices.Clear();
        }

        /// <summary>
        /// closeDevice_Click: Action to take when 'Disconnect and Refresh List' is clicked on
        /// - Cancel all read operations
        /// - Close and dispose the SerialDevice object
        /// - Enumerate connected devices
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void closeDevice_Click(object sender, RoutedEventArgs e) {
            try {
                status.Text = "";
                baudCombox.IsEnabled = true;
                CancelReadTask();
                CloseDevice();
                ListAvailablePorts();
            } catch (Exception ex1) {
                status.Text = ex1.Message;
            }
        }

        private void comPortRefresh_Click(object sender, RoutedEventArgs e) {
            ListAvailablePorts();
        }

        private async void selectFileButton_Click(object sender, RoutedEventArgs e) {
            status.Text = "Select a file";
            FileOpenPicker openPicker = new FileOpenPicker();
            openPicker.ViewMode = PickerViewMode.Thumbnail;
            openPicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            openPicker.FileTypeFilter.Add("*");
            StorageFile file = await openPicker.PickSingleFileAsync();

            if (file != null) {
                // Application now has read/write access to the picked file
                status.Text = "Selected " + file.Name;
                ViewModel.SelectedFileName = "Loaded " + file.Name;

                byte[] text = await Utils.ReadFile(file);
                string res;
                res = BitConverter.ToString(text);
                ViewModel.TextToSend = res;
            }
        }


        private async void savePacketButton_Click(object sender, RoutedEventArgs e) {
            var savePicker = new FileSavePicker();
            var packets = receivedPacketsView.SelectedItems;
            savePicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            // Dropdown of file types the user can save the file as
            savePicker.FileTypeChoices.Add("Binary file", new List<string>() { ".bin" });
            // Default file name if the user does not type one in or select a file to replace
            savePicker.SuggestedFileName = "Packet" + receivedPacketsView.SelectedIndex;

            StorageFile file = await savePicker.PickSaveFileAsync();
            if (file != null) {
                // Prevent updates to the remote version of the file until
                // we finish making changes and call CompleteUpdatesAsync.
                CachedFileManager.DeferUpdates(file);
                var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
                // write to file
                using (var outputStream = stream.GetOutputStreamAt(0)) {
                    using (var dataWriter = new DataWriter(outputStream)) {
                        foreach (Packet packet in packets) {
                            int padding = Constants.PAYLOADLEN - packet.payload.Length;
                            byte[] nullPadding = new byte[padding];
                            dataWriter.WriteBytes(packet.payload);
                            //dataWriter.WriteBytes(nullPadding);
                        }
                        await dataWriter.StoreAsync();
                        await outputStream.FlushAsync();
                    }
                }
                stream.Dispose();
                // Let Windows know that we're finished changing the file so
                // the other app can update the remote version of the file.
                // Completing updates may require Windows to ask for user input.
                Windows.Storage.Provider.FileUpdateStatus res =
                    await CachedFileManager.CompleteUpdatesAsync(file);
                if (res == Windows.Storage.Provider.FileUpdateStatus.Complete) {
                    status.Text = "File " + file.Name + " was saved.";
                } else {
                    status.Text = "File " + file.Name + " couldn't be saved.";
                }
            } else {
                status.Text = "Operation cancelled.";
            }
        }

        private void selectAllButton_Click(object sender, RoutedEventArgs e) {
            bool b = receivedPacketsView.SelectedItems.Count > 0;
            if (b) {
                receivedPacketsView.SelectionMode = ListViewSelectionMode.None;
                receivedPacketsView.SelectionMode = ListViewSelectionMode.Multiple;
            }
            else {
                receivedPacketsView.SelectionMode = ListViewSelectionMode.Multiple;
                receivedPacketsView.SelectAll();
            }

        }

        private void receivedPacketsView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (receivedPacketsView.SelectedItem != null)
                savePacketButton.IsEnabled = true;
            else
                savePacketButton.IsEnabled = false;
        }

        private void writeTextButton_Click(object sender, RoutedEventArgs e) {
            if (serialPort != null) {
                dataWriteObject = new DataWriter(serialPort.OutputStream);
            } else if (clientSocket != null) {
                dataWriteObject = new DataWriter(clientSocket.OutputStream);
            } else {
                status.Text = "Select a device and connect";
                return;
            }
            SendText();
        }


        private async void SendText() { 
            try {
                // Create the DataWriter object and attach to OutputStream
                string text = ViewModel.TextToSend;
                int repeat = 1;
                byte[] payload;

                if (!(bool)AsciiCheckBox.IsChecked) {
                    text = Regex.Replace(text, "[^0-9a-fA-F]+", "");

                    //Launch the WriteAsync task to perform the write
                    if (text.Length % 2 != 0) {
                        status.Text = "Invalid hexadecimal payload";
                        return;
                    }
                    payload = Utils.StringToByteArray(text);
                } else {
                    payload = Encoding.ASCII.GetBytes(text);
                }
                
                for (int i = 0; i < repeat; i++) 
                    await WriteAsync(payload);
            } catch (Exception ex1) {
                status.Text = "writeTextButton_Click: " + ex1.Message;
            } finally {
                // Cleanup once complete
                if (dataWriteObject != null)
                {
                    dataWriteObject.DetachStream();
                    dataWriteObject = null;
                }
            }
        }


        private void baudCombox_SelectionChanged(object sender, SelectionChangedEventArgs e) {
           ApplicationDataCompositeValue composite = (ApplicationDataCompositeValue)localSettings.Values[Constants.SETTINGSKEY];
            if (composite == null)
                return;
            composite[Constants.BAUDKEY] = baudCombox.SelectedIndex;
            localSettings.Values[Constants.SETTINGSKEY] = composite;
        }

        private void commandCombox_SelectionChanged(object sender, SelectionChangedEventArgs e) {
           ApplicationDataCompositeValue composite = (ApplicationDataCompositeValue)localSettings.Values[Constants.SETTINGSKEY];
            if (composite == null)
                return;
            composite[Constants.COMMANDCHOICEKEY] = commandCombox.SelectedIndex;
            localSettings.Values[Constants.SETTINGSKEY] = composite;

            commandCombox.Opacity = 1.0;
            commandCombox.FontWeight = FontWeights.SemiBold;
        }

        private void sendrcv_TextChanged(object sender, TextChangedEventArgs e) {
           ApplicationDataCompositeValue composite = (ApplicationDataCompositeValue)localSettings.Values[Constants.SETTINGSKEY];
            if (composite == null)
                return;
            composite[Constants.SENDERKEY] = senderAdd.Text;
            composite[Constants.RECEIVERKEY] = receiverAdd.Text;
            localSettings.Values[Constants.SETTINGSKEY] = composite;
        }


        private async void SaveNewConfigFile() {
            StorageFolder localFolder = ApplicationData.Current.LocalFolder;
            StorageFile file = await localFolder.CreateFileAsync("config.csv", CreationCollisionOption.ReplaceExisting);
            string csv = pMan.CommandsToCSV();
            await FileIO.WriteTextAsync(file, csv);
        }

        private void SetCommandList(List<Tuple<UInt16, string>> dict) {
            ViewModel.ListOfCommands = new ObservableCollection<Command>();
            if (pMan == null)
                pMan = new PacketManager(dict);
            else
                pMan.CommandNames = dict;
            foreach(Tuple<ushort, string> entry in dict) {
                ViewModel.ListOfCommands.Add(new Command(entry.Item2, entry.Item1));
            }
            //TODO: Prova a mettere questa roba nella classe HostViewModel, con notifica automatica dei cambiamenti
            commandCombox.ItemsSource = ViewModel.ListOfCommands;
            if (ViewModel.ListOfCommands.Count > 0) {
                commandCombox.SelectedIndex = 0;
            }
        }


        private void AddressHexValidation(object s, TextChangedEventArgs args) {
            TextBox sender = (TextBox)s;
            Command comm = (Command)commandCombox.SelectedItem;
            if (!Regex.IsMatch(sender.Text, @"\A\b[0-9a-fA-F]+\b\Z") || sender.Text.Length > 2) {
                int pos = sender.SelectionStart - 1;
                if (pos < 0)
                    return;
                sender.Text = sender.Text.Remove(pos, 1);
                sender.SelectionStart = pos;
                //commandCombox.SelectedIndex = -1;
            }

            if (CommandCode.Text.Length == 2 && SubCommandCode.Text.Length == 2) {
                foreach (Command c in ViewModel.ListOfCommands) {
                    if (c.MainCode == CommandCode.Text && c.SubCode == SubCommandCode.Text) {
                        commandCombox.SelectedItem = c;
                        commandCombox.Opacity = 1.0;
                        commandCombox.FontWeight = FontWeights.SemiBold;
                        return;
                    }
                }
            }

            if (comm != null) {
                if (CommandCode.Text != comm.MainCode || SubCommandCode.Text != comm.SubCode) {
                    commandCombox.Opacity = 0.7;
                    commandCombox.FontWeight = FontWeights.Normal;
                }
            }
       }

        private async void ConfigButton_Click(object sender, RoutedEventArgs e) {
            status.Text = "Select a file";
            FileOpenPicker openPicker = new FileOpenPicker();
            openPicker.ViewMode = PickerViewMode.Thumbnail;
            openPicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            openPicker.FileTypeFilter.Add(".txt");
            openPicker.FileTypeFilter.Add(".csv");
            StorageFile file = await openPicker.PickSingleFileAsync();

            if (file != null) {
                // Application now has read/write access to the picked file
                string text = await FileIO.ReadTextAsync(file);
                try {
                    SetCommandList(Utils.readCSV(text));
                    SaveNewConfigFile();
                }
                catch (Exception ex1) {
                    status.Text = "Invalid CSV file: " + ex1.ToString();
                    SetCommandList(new List<Tuple<ushort, string>>());

                    return;
                }
           } else {
                SetCommandList(new List<Tuple<ushort, string>>());
            }
        }

        private async void clearListButton_Click(object sender, RoutedEventArgs e) {
            if (pMan.receivedPackets.Count == 0)
                return;

            ContentDialog deleteFileDialog = new ContentDialog() {
                Title = "Clear packet history?",
                Content = "Are you sure you wan to delete the list of received packets?",
                PrimaryButtonText = "Cancel",
                SecondaryButtonText = "Ok"
            };

            ContentDialogResult result = await deleteFileDialog.ShowAsync();

            // Delete the file if the user clicked the primary button. 
            /// Otherwise, do nothing. 
            if (result == ContentDialogResult.Secondary) {
                pMan.ClearHistory();
            }
        }

        private async void SendFileSection() {
            var stream = await SelectedFile.OpenAsync(FileAccessMode.Read);
            ulong size = stream.Size;

            if (readingIndex >= stream.Size) {
                readingIndex = 0;
                SelectedFile = null;
                // Cleanup once complete
                return;
            }
            status.Text = "Sending packet " + (readingIndex /(ulong) Constants.PAYLOADLEN);

            try {
                if (serialPort != null) {
                    // Create the DataWriter object and attach to OutputStream
                    dataWriteObject = new DataWriter(serialPort.OutputStream);
                }
                else {
                    status.Text = "Select a device and connect";
                }
                using (var inputStream = stream.GetInputStreamAt(readingIndex)) {
                    using (var dataReader = new DataReader(inputStream)) {
                        uint numBytesLoaded = await dataReader.LoadAsync((uint)Constants.PAYLOADLEN);
                        byte[] payload = new byte[numBytesLoaded];
                        dataReader.ReadBytes(payload);
                        await WriteAsync(payload);
                        readingIndex += numBytesLoaded;
                    }
                }
            }
            catch (Exception ex1) {
                status.Text = "writeTextButton_Click: " + ex1.Message;
            }
            finally {
                // Cleanup once complete
                if (dataWriteObject != null) {
                    dataWriteObject.DetachStream();
                    dataWriteObject = null;
                }
                int curProg = int.Parse(SubCommandCode.Text) + 1;
                SubCommandCode.Text = curProg.ToString();
            }
        }

        private async void SendAllButton_Click(object sender, RoutedEventArgs e) {
            status.Text = "Select a file";
            FileOpenPicker openPicker = new FileOpenPicker();
            openPicker.ViewMode = PickerViewMode.Thumbnail;
            openPicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            openPicker.FileTypeFilter.Add(".bin");
            StorageFile file = await openPicker.PickSingleFileAsync();

            if (file != null)
            {
                SelectedFile = file;
                readingIndex = 0;
               SendFileSection();
           }
        }

        private string oldText;

        private void sendText_KeyDown(object sender, Windows.UI.Xaml.Input.KeyRoutedEventArgs e) {
            ViewModel.TextToSend = sendText.Text;
            string newText = sendText.Text;
            /*if (e.Key == Windows.System.VirtualKey.Enter) {
                sendTextUART();
                e.Handled = true;
            } else {*/
               if (!(bool)AsciiCheckBox.IsChecked) {
                    if (!Utils.IsKeyHex(e.Key)) {
                        e.Handled = true;
                    }
                }
            //}
            oldText = ViewModel.TextToSend; 
        }

        private void AsciiCheckBox_Unchecked(object sender, RoutedEventArgs e) {
            byte[] ba = Encoding.ASCII.GetBytes(sendText.Text);
            var hexString = BitConverter.ToString(ba).Replace("-", " ");
            
            ViewModel.TextToSend = hexString;
        }

        private void AsciiCheckBox_Checked(object sender, RoutedEventArgs e) {
            string text = ViewModel.TextToSend;
            text = Regex.Replace(text, "[^0-9a-fA-F]+", "");
            text = Utils.ConvertHex(text);
            ViewModel.TextToSend = text;
        }

        private void SerialSockCombobox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (serialStackPanel == null || socketStackPanel == null)
            {
                return;
            }
            if (SerialSockCombobox.SelectedIndex == 0)
            {
                serialStackPanel.Visibility = Visibility.Visible;
                socketStackPanel.Visibility = Visibility.Collapsed;
            } else
            {
                serialStackPanel.Visibility = Visibility.Collapsed;
                socketStackPanel.Visibility = Visibility.Visible;
            }
        }

        private void sockConnect_Click(object sender, RoutedEventArgs e) {
            var selection = ConnectDevices.SelectedItems;
            string ip = ipAddrTextBox.Text;
            string port = portTextBox.Text;

            //TODO check for correct input

            ApplicationDataCompositeValue composite = (ApplicationDataCompositeValue)localSettings.Values[Constants.SETTINGSKEY];
            if (composite != null) {
                composite[Constants.IPADDR] = ipAddrTextBox.Text;
                composite[Constants.PORT] = portTextBox.Text;
                localSettings.Values[Constants.SETTINGSKEY] = composite;
            }


            try {
                status.Text = "Waiting for network...";
                // Set the RcvdText field to invoke the TextChanged callback
                // The callback launches an async Read task to wait for data
                rcvdText.Text = "Waiting for data...";


                // Create cancellation token object to close I/O operations when closing the device
                ReadCancellationTokenSource = new CancellationTokenSource();

                // Enable 'WRITE' button to allow sending data
                ConnectDeviceFlyout.Hide();

                ListenSock(ip, port);
            } catch (Exception ex1) {
                status.Text = ex1.Message;
            }
        }

        private void closeSock_Click(object sender, RoutedEventArgs e) {
            CancelReadTask();
            if (clientSocket != null)
                clientSocket.Dispose();
            clientSocket = null;
        }

        private void writeTextButton_PointerEntered(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e) {

        }
    }
}
