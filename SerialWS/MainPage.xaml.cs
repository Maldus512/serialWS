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

namespace SerialWS
{

    public class HostViewModel : INotifyPropertyChanged
    {
        private string selectedFileName;
        private ObservableCollection<Command> listOfCommands;

        public event PropertyChangedEventHandler PropertyChanged = delegate { };

        public HostViewModel()
        {
            this.selectedFileName = "Please open a file";
        }

        public ObservableCollection<Command> ListOfCommands {
            get { return this.listOfCommands; }
            set {
                this.listOfCommands = value;
                this.OnPropertyChanged("ListOfCommands");
            }
        }

        public string SelectedFileName
        {
            get { return this.selectedFileName; }
            set
            {
                this.selectedFileName = value;
                this.OnPropertyChanged("SelectedFileName");
            }
        }

        public void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            // Raise the PropertyChanged event, passing the name of the property whose value has changed.
            this.PropertyChanged(this, new PropertyChangedEventArgs(propertyName));
        }
    }


    public sealed partial class MainPage : Page
    {
        /// <summary>
        /// Private variables
        /// </summary>
        private SerialDevice serialPort = null;
        DataWriter dataWriteObject = null;
        DataReader dataReaderObject = null;
        static Array baudRate = new uint[] { 4800, 7200, 9600, 56000, 76800, 115200 };


        public HostViewModel ViewModel { get; set; } = new HostViewModel();


        private ObservableCollection<DeviceInformation> listOfDevices;
        
        private CancellationTokenSource ReadCancellationTokenSource;

        private PacketManager pMan;

        private byte[] readBuffer;

        private StorageFile SelectedFile;

        Windows.Storage.ApplicationDataContainer localSettings;

        public MainPage()
        {
            pMan = new PacketManager(new Dictionary<ushort, string>());
            this.InitializeComponent();
            this.DataContext = this;

            comPortInput.IsEnabled = false;

            restoreSettings();

            ListAvailablePorts();
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
            }

            Dictionary<UInt16, string> commands = new Dictionary<ushort, string>();
            try {
                StorageFile file = await localFolder.GetFileAsync("config.csv");
                string text = await FileIO.ReadTextAsync(file);
                commands = Utils.readCSV(text);
                SetCommandList(commands);
                //tempsavecomm();
            }
            catch (Exception ex) {
                status.Text = ex.ToString();
            }

            baudRateSource.Source = baudRate;
            baudCombox.SelectedIndex = (int)composite[Constants.BAUDKEY];
            senderAdd.Text = (string)composite[Constants.SENDERKEY];
            receiverAdd.Text = (string)composite[Constants.RECEIVERKEY];

            localSettings.Values[Constants.SETTINGSKEY] = composite;
        }

        /// <summary>
        /// ListAvailablePorts
        /// - Use SerialDevice.GetDeviceSelector to enumerate all serial devices
        /// - Attaches the DeviceInformation to the ListBox source so that DeviceIds are displayed
        /// </summary>
        private async void ListAvailablePorts()
        {
            listOfDevices = new ObservableCollection<DeviceInformation>();
            try
            {
                string aqs = SerialDevice.GetDeviceSelector();
                var dis = await DeviceInformation.FindAllAsync(aqs);

                status.Text = "Select a device and connect";

                for (int i = 0; i < dis.Count; i++)
                {
                    listOfDevices.Add(dis[i]);
                }

                DeviceListSource.Source = listOfDevices;
                comPortInput.IsEnabled = true;
                ConnectDevices.SelectedIndex = -1;
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
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
        private async void comPortInput_Click(object sender, RoutedEventArgs e)
        {
            var selection = ConnectDevices.SelectedItems;

            if (selection.Count <= 0)
            {
                status.Text = "Select a device and connect";
                return;
            }

            DeviceInformation entry = (DeviceInformation)selection[0];

            try
            {
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

                baudCombox.IsEnabled = false;
                Listen();
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
                comPortInput.IsEnabled = true;
            }
        }

        
        /// <summary>
        /// WriteAsync: Task that asynchronously writes data from the input text box 'sendText' to the OutputStream 
        /// </summary>
        /// <returns></returns>
        private async Task WriteAsync(byte[] text)
        {
            Task<UInt32> storeAsyncTask;
            byte a, b;
            byte[] c = new byte[2];
            ushort tmp;
            tmp = ((Command)commandCombox.SelectedItem).Code;
            c[1] = (byte)tmp;
            c[0] = (byte)(tmp >> 8);

            try
            {
                b = (byte)int.Parse(senderAdd.Text, System.Globalization.NumberStyles.HexNumber);
                a = (byte)int.Parse(receiverAdd.Text, System.Globalization.NumberStyles.HexNumber);
            }
            catch (FormatException)
            {
                status.Text = "Wrong sender/receiver address";
                return;
            }
            List<byte[]> toSend = Utils.formPackets(text, b, a, c);

            foreach (byte[] packet in toSend)
            {
                // Load the text from the sendText input text box to the dataWriter object
                //dataWriteObject.WriteString(text);
                dataWriteObject.WriteBytes(packet);

                // Launch an async task to complete the write operation
                storeAsyncTask = dataWriteObject.StoreAsync().AsTask();

                UInt32 bytesWritten = await storeAsyncTask;
                if (bytesWritten > 0)
                {
                    //status.Text = sendText.Text + ", ";
                    status.Text = bytesWritten + " bytes written successfully!";
                }
                sendText.Text = "";
            }
        }

        /// <summary>
        /// - Create a DataReader object
        /// - Create an async task to read from the SerialDevice InputStream
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void Listen()
        {
            try
            {
                if (serialPort != null)
                {
                    dataReaderObject = new DataReader(serialPort.InputStream);

                    readBuffer = new byte[4096];

                    // keep reading the serial input
                    while (true)
                    {
                        await ReadAsync(ReadCancellationTokenSource.Token);
                    }
                }
            }
            catch (Exception ex)
            {
                if (ex.GetType().Name == "TaskCanceledException")
                {
                    status.Text = "Reading task was cancelled, closing device and cleaning up";
                    CloseDevice();
                }
                else
                {
                    status.Text = ex.Message;
                }
            }
            finally
            {
                // Cleanup once complete
                if (dataReaderObject != null)
                {
                    dataReaderObject.DetachStream();
                    dataReaderObject = null;
                }
            }
        }

        /// <summary>
        /// ReadAsync: Task that waits on data and reads asynchronously from the serial device InputStream
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task ReadAsync(CancellationToken cancellationToken)
        {
            Task<UInt32> loadAsyncTask;
            byte[] text;
            uint ReadBufferLength = 4096;

            // If task cancellation was requested, comply
            cancellationToken.ThrowIfCancellationRequested();

            // Set InputStreamOptions to complete the asynchronous read operation when one or more bytes is available
            dataReaderObject.InputStreamOptions = InputStreamOptions.Partial;

            // Create a task object to wait for data on the serialPort.InputStream
            loadAsyncTask = dataReaderObject.LoadAsync(ReadBufferLength).AsTask(cancellationToken);

            // Launch the task and wait
            UInt32 bytesRead = await loadAsyncTask;
            if (bytesRead > 0)
            {
                text = new byte[bytesRead];
                dataReaderObject.ReadBytes(text);
                rcvdText.Text = BitConverter.ToString(text);
                status.Text = bytesRead + " bytes read successfully!";

                //packet = pMan.validatePacket(text);
                pMan.evalNewData(text);
            }
        }

        /// <summary>
        /// CancelReadTask:
        /// - Uses the ReadCancellationTokenSource to cancel read operations
        /// </summary>
        private void CancelReadTask()
        {
            if (ReadCancellationTokenSource != null)
            {
                if (!ReadCancellationTokenSource.IsCancellationRequested)
                {
                    ReadCancellationTokenSource.Cancel();
                }
            }
        }

        /// <summary>
        /// CloseDevice:
        /// - Disposes SerialDevice object
        /// - Clears the enumerated device Id list
        /// </summary>
        private void CloseDevice()
        {
            if (serialPort != null)
            {
                serialPort.Dispose();
            }
            serialPort = null;

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
        private void closeDevice_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                status.Text = "";
                baudCombox.IsEnabled = true;
                CancelReadTask();
                CloseDevice();
                ListAvailablePorts();
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
            }
        }

        private void comPortRefresh_Click(object sender, RoutedEventArgs e)
        {
            ListAvailablePorts();
        }

        private async void selectFileButton_Click(object sender, RoutedEventArgs e)
        {
            status.Text = "Select a file";
            FileOpenPicker openPicker = new FileOpenPicker();
            openPicker.ViewMode = PickerViewMode.Thumbnail;
            openPicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            openPicker.FileTypeFilter.Add("*");
            StorageFile file = await openPicker.PickSingleFileAsync();

            if (file != null)
            {
                // Application now has read/write access to the picked file
                SelectedFile = file;
                status.Text = "Selected " + file.Name;
                ViewModel.SelectedFileName = "Loaded " + file.Name;

                byte[] text = await Utils.ReadFile(file);
                sendText.Text = BitConverter.ToString(text);
            }
        }

        private async void tempsavecomm() {
             var savePicker = new FileSavePicker();
            savePicker.SuggestedStartLocation = PickerLocationId.Desktop;
            savePicker.FileTypeChoices.Add("text", new List<string>() { ".txt" });
            savePicker.SuggestedFileName = "config";

            StorageFile file = await savePicker.PickSaveFileAsync();
            if (file != null)
            {
                CachedFileManager.DeferUpdates(file);
                await FileIO.WriteTextAsync(file, pMan.CommandsToCSV());
                Windows.Storage.Provider.FileUpdateStatus res =
                    await CachedFileManager.CompleteUpdatesAsync(file);
            }

        }

        private async void savePacketButton_Click(object sender, RoutedEventArgs e)
        {
            var savePicker = new FileSavePicker();
            var packet = (Packet) receivedPacketsView.SelectedItem;
            savePicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            // Dropdown of file types the user can save the file as
            savePicker.FileTypeChoices.Add("Binary file", new List<string>() { ".bin" });
            // Default file name if the user does not type one in or select a file to replace
            savePicker.SuggestedFileName = "Packet" + receivedPacketsView.SelectedIndex;

            StorageFile file = await savePicker.PickSaveFileAsync();
            if (file != null)
            {
                // Prevent updates to the remote version of the file until
                // we finish making changes and call CompleteUpdatesAsync.
                CachedFileManager.DeferUpdates(file);
                // write to file
                await FileIO.WriteBytesAsync(file, packet.payload);
                // Let Windows know that we're finished changing the file so
                // the other app can update the remote version of the file.
                // Completing updates may require Windows to ask for user input.
                Windows.Storage.Provider.FileUpdateStatus res =
                    await CachedFileManager.CompleteUpdatesAsync(file);
                if (res == Windows.Storage.Provider.FileUpdateStatus.Complete)
                {
                    status.Text = "File " + file.Name + " was saved.";
                }
                else
                {
                    status.Text = "File " + file.Name + " couldn't be saved.";
                }
            }
            else
            {
                status.Text = "Operation cancelled.";
            }
        }

        private void receivedPacketsView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (receivedPacketsView.SelectedItem != null)
                savePacketButton.IsEnabled = true;
            else
                savePacketButton.IsEnabled = false;
        }

        private async void writeTextButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (serialPort != null)
                {
                    // Create the DataWriter object and attach to OutputStream
                    dataWriteObject = new DataWriter(serialPort.OutputStream);
                    string text = sendText.Text;
                    text = Regex.Replace(text, "[^0-9a-fA-F]+", "");

                    //Launch the WriteAsync task to perform the write
                    if (text.Length % 2 != 0) {
                        status.Text = "Invalid hexadecimal payload";
                        return;
                    }
                    byte[] payload = Utils.StringToByteArray(text);
                    await WriteAsync(payload);
                }
                else
                {
                    status.Text = "Select a device and connect";
                }
            }
            catch (Exception ex)
            {
                status.Text = "writeTextButton_Click: " + ex.Message;
            }
            finally
            {
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

        private void SetCommandList(Dictionary<UInt16, string> dict) {
            ViewModel.ListOfCommands = new ObservableCollection<Command>();
            if (pMan == null)
                pMan = new PacketManager(dict);
            else
                pMan.CommandNames = dict;
            foreach(KeyValuePair<ushort, string> entry in dict) {
                ViewModel.ListOfCommands.Add(new Command(entry.Value, entry.Key));
            }
            //TODO: Prova a mettere questa roba nella classe HostViewModel, con notifica automatica dei cambiamenti
            //commandCombox.ItemsSource = ViewModel.ListOfCommands;
            if (ViewModel.ListOfCommands.Count > 0) {
                commandCombox.SelectedIndex = 0;
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

            if (file != null)
            {
                // Application now has read/write access to the picked file
                string text = await FileIO.ReadTextAsync(file);
                try {
                    SetCommandList(Utils.readCSV(text));
                    SaveNewConfigFile();
                }
                catch (Exception ex) {
                    status.Text = "Invalid CSV file: " + ex.ToString();
                    return;
                }
           }
        }
    }
}
