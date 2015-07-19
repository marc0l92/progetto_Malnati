﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace sync_server
{
    class ClientManager
    {
        private BackgroundWorker clientThread;
        private StateObject stateClient;
        private SyncClient client = new SyncClient();
        private AsyncManagerServer.StatusDelegate statusDelegate;
        private ManualResetEvent receiveDone = new ManualResetEvent(false);
        private Boolean syncEnd = false;
        private Boolean wellEnd = false;
        private List<FileChecksum> userChecksum;
        private List<FileChecksum> TEMP;
        private SyncCommand cmd;
        private SyncSQLite mySQLite;
        private String serverDir;
        private Boolean stopped = false;
        private int maxVersionNumber;

        public ClientManager(Socket sock, String workDir, int maxVers, AsyncManagerServer.StatusDelegate sd)
        {
            statusDelegate = sd;
            stateClient = new StateObject();
            stateClient.workSocket = sock;
            maxVersionNumber = maxVers;
            serverDir = workDir;
            client.usrNam = "NOACTVIVE";
            client.usrID = -1;
            mySQLite = new SyncSQLite();
            clientThread = new BackgroundWorker();
            clientThread.DoWork += new DoWorkEventHandler(doClient);
            clientThread.RunWorkerCompleted += new RunWorkerCompletedEventHandler(stop);
            clientThread.RunWorkerAsync();
        }

        public void stop(object sender, RunWorkerCompletedEventArgs e)
        {
            // todo Cosa succede se sto sincronizzando? devo fare un restore?
            stateClient.workSocket.Close();
            AsyncManagerServer.DecreaseClient();
            AsyncManagerServer.PrintClient();
            statusDelegate("Server Stopped ", fSyncServer.LOG_INFO);
            mySQLite.closeConnection();
            if (TEMP != null && (TEMP.Count > 0))
            {
                foreach (FileChecksum check in TEMP)
                {
                    File.Delete(check.FileNameServer);
                    statusDelegate("Delete File: " + check.FileNameServer, fSyncServer.LOG_INFO);
                }
                TEMP.Clear();
            }

        }
        public void StopService()
        {
            syncEnd = true;
            stopped = true;
            wellEnd = false;
        }
        public void WellStop()
        {
            // todo Cosa succede se sto sincronizzando? devo fare un restore?
            syncEnd = true;
            stopped = true;
            wellEnd = true;
        }



        public void setStatusDelegate(AsyncManagerServer.StatusDelegate sd)
        {
            statusDelegate = sd;
        }

        private void doClient(object sender, DoWorkEventArgs e)
        {
            while (!syncEnd)
            {
                if (!SocketConnected(stateClient.workSocket))
                {
                    StopService();
                    break;
                }
                receiveDone.Reset();
                // Receive the response from the remote device.
                this.ReceiveCommand(stateClient.workSocket);
                if (!stopped)
                {
                    receiveDone.WaitOne();
                    if (doCommand())
                        statusDelegate("Slave Thread Done Command Successfully ", fSyncServer.LOG_INFO);
                    else
                        statusDelegate("Slave Thread Done Command with no Success", fSyncServer.LOG_INFO);
                }
                else break;
            }
            if (!wellEnd)
                statusDelegate("All NOT Well End Terminated", fSyncServer.LOG_INFO);
            else
                statusDelegate("All Well End Terminated", fSyncServer.LOG_INFO);

        }

        public void ReceiveCommand(Socket client)
        {
            try
            {
                if (!SocketConnected(stateClient.workSocket))
                    StopService();
                // Begin receiving the data from the remote device.
                IAsyncResult iAR = client.BeginReceive(stateClient.buffer, 0, StateObject.BufferSize, 0,
                    new AsyncCallback(ReceiveCallback), null);
                bool success = iAR.AsyncWaitHandle.WaitOne(10000, true);

                if (!success)
                {
                    statusDelegate("Timeout Expired", fSyncServer.LOG_INFO);
                    StopService();
                    receiveDone.Set();
                }
            }
            catch (Exception e)
            {
                statusDelegate("Exception: " + e.Message, fSyncServer.LOG_INFO);
                if (stopped)
                    statusDelegate("DON'T WORRY SERVER IT'S BEEN STOPPED BY CONNECTION CLOSE, IT'S ALL FINE (Receive Command)", fSyncServer.LOG_INFO);
                else
                    StopService();
            }
        }

        public void ReceiveCallback(IAsyncResult ar)
        {
            try
            {
                // Retrieve the state object and the client socket
                // from the asynchronous state object.
                // StateObject state = (StateObject)ar.AsyncState;
                // Socket client = state.workSocket;

                // Read data from the remote device.
                if ((!syncEnd)&&stateClient.workSocket.Connected==true)
                {
                    int bytesRead = stateClient.workSocket.EndReceive(ar);

                    if ((bytesRead > 0))
                    {
                        // There might be more data, so store the data received so far.
                        stateClient.sb.Append(Encoding.ASCII.GetString(stateClient.buffer, 0, bytesRead));
                    }
                    if (SyncCommand.searchJsonEnd(stateClient.sb.ToString()) == -1)
                    {
                        // Get the rest of the data.
                        stateClient.workSocket.BeginReceive(stateClient.buffer, 0, StateObject.BufferSize, 0,
                            new AsyncCallback(ReceiveCallback), null);
                    }
                    if (!SocketConnected(stateClient.workSocket) || stateClient.workSocket.Connected==false)
                    {
                        receiveDone.Set();
                    }
                    else
                    {
                        // All the data has arrived; put it in response.
                        if (stateClient.sb.Length > 1)
                        {
                            cmd = SyncCommand.convertFromString(stateClient.sb.ToString());
                            stateClient.sb.Clear();
                            SendCommand(stateClient.workSocket, new SyncCommand(SyncCommand.CommandSet.ACK));
                        }
                        // Signal that all bytes have been received.
                        receiveDone.Set();
                    }
                }
                else receiveDone.Set();
            }
            catch (Exception e)
            {
                statusDelegate("Exception: " + e.Message, fSyncServer.LOG_INFO);
                if(stopped)
                    statusDelegate("DON'T WORRY SERVER IT'S BEEN STOPPED BY CONNECTION CLOSE, IT'S ALL FINE (Receive Callback)", fSyncServer.LOG_INFO);
                else
                    StopService();
            }
        }

        public Boolean doCommand()
        {
            if (cmd != null)
            {
                    switch (cmd.Type)
                    {
                        case SyncCommand.CommandSet.LOGIN:
                            statusDelegate(" Command Login ", fSyncServer.LOG_INFO);
                            return LoginUser();
                        case SyncCommand.CommandSet.START:
                            statusDelegate(" Command Start ", fSyncServer.LOG_INFO);
                            return StartSession();
                        case SyncCommand.CommandSet.GET:
                              if ((client.usrNam == "NOACTIVE")&&(client.usrID==-1))
                              {
                                  statusDelegate(" USER IS NOT LOGGED IN TO PERFORM A GET ", fSyncServer.LOG_INFO);
                                  return true;
                              }
                            statusDelegate(" Command Get ", fSyncServer.LOG_INFO);
                            return SendFileClient();
                        case SyncCommand.CommandSet.RESTORE:
                            if ((client.usrNam == "NOACTIVE") && (client.usrID == -1))
                            {
                                statusDelegate(" USER IS NOT LOGGED IN TO PERFORM A RESTORE ", fSyncServer.LOG_INFO);
                                return true;
                            }
                            statusDelegate(" Command Restore ", fSyncServer.LOG_INFO);
                            return RestoreVersion();
                        case SyncCommand.CommandSet.ENDSYNC:
                            statusDelegate("Command EndSync ", fSyncServer.LOG_INFO);
                            return EndSync();
                        case SyncCommand.CommandSet.NOSYNC:
                            statusDelegate("Command NoSync ", fSyncServer.LOG_INFO);
                            return NoSync();
                        case SyncCommand.CommandSet.DEL:
                            if ((client.usrNam == "NOACTIVE") && (client.usrID == -1))
                            {
                                statusDelegate(" USER IS NOT LOGGED IN TO PERFORM A DELETE ", fSyncServer.LOG_INFO);
                                return true;
                            }
                            statusDelegate("Command Delete ", fSyncServer.LOG_INFO);
                            return DeleteFile();
                        case SyncCommand.CommandSet.NEW:
                            if ((client.usrNam == "NOACTIVE") && (client.usrID == -1))
                            {
                                statusDelegate(" USER IS NOT LOGGED IN TO PERFORM A NEW ", fSyncServer.LOG_INFO);
                                return true;
                            }
                            statusDelegate(" Command New ", fSyncServer.LOG_INFO);
                            return NewFile();
                        case SyncCommand.CommandSet.EDIT:
                            if ((client.usrNam == "NOACTIVE") && (client.usrID == -1))
                            {
                                statusDelegate(" USER IS NOT LOGGED IN TO PERFORM A EDIT ", fSyncServer.LOG_INFO);
                                return true;
                            }
                            statusDelegate("Command Edit ", fSyncServer.LOG_INFO);
                            return EditFile();
                        case SyncCommand.CommandSet.NEWUSER:
                            statusDelegate(" Command NewUser ", fSyncServer.LOG_INFO);
                            return NewUser();
                        case SyncCommand.CommandSet.GETVERSIONS:
                            if ((client.usrNam == "NOACTIVE") && (client.usrID == -1))
                            {
                                statusDelegate(" USER IS NOT LOGGED IN TO PERFORM A GET VERSION ", fSyncServer.LOG_INFO);
                                return true;
                            }
                            statusDelegate("Command Edit ", fSyncServer.LOG_INFO);
                            return GetVersions();
                        default:
                            statusDelegate("Recieved Wrong Command", fSyncServer.LOG_INFO); //TODO return false and manage difference
                            StopService();
                            return true;
                    }
                
            }
            else
            { 
                statusDelegate("Null Command Received", fSyncServer.LOG_INFO);
                return true;
            }

        }
        public Boolean LoginUser()
        {
            statusDelegate("Get user data on DB (LoginUser)", fSyncServer.LOG_INFO);
            Int64 userID = mySQLite.authenticateUser(cmd.Username, cmd.Password);
            if (userID >= 0) //Call DB Authentication User
            {
                statusDelegate("User Credential Confermed (LoginUser)", fSyncServer.LOG_INFO);
                client.usrID = userID;
                serverDir += "\\user" + client.usrID;
                client.usrNam = cmd.Username;
                client.usrPwd = cmd.Password;
                //client.vers = mySQLite.getUserLastVersion();
                SendCommand(stateClient.workSocket, new SyncCommand(SyncCommand.CommandSet.AUTHORIZED));
                statusDelegate("Send Back Authorized Message (LoginUser)", fSyncServer.LOG_INFO);
                return true;
            }
            else
            {
                statusDelegate("User Credential NOT Confirmed (LoginUser)", fSyncServer.LOG_INFO);
                SendCommand(stateClient.workSocket, new SyncCommand(SyncCommand.CommandSet.UNAUTHORIZED));
                statusDelegate("Send Back Unauthorized Message (LoginUser)", fSyncServer.LOG_INFO);
                return true;
            }
        }

        public Boolean NewUser()
        {
            Int64 userID = mySQLite.newUser(cmd.Username, cmd.Password, cmd.Directory);
            if (userID == -1) //Call DB New User
            {

                statusDelegate("Username in CONFLICT choose another one (NewUser)", fSyncServer.LOG_INFO);
                SendCommand(stateClient.workSocket, new SyncCommand(SyncCommand.CommandSet.UNAUTHORIZED));
                statusDelegate("Send Back Unauthorized Message (NewUser)", fSyncServer.LOG_INFO);
                return true;
            }
            else
            {
                statusDelegate("User Added Succesfully (NewUser)", fSyncServer.LOG_INFO);
                client.usrID = userID;
                client.usrNam = cmd.Username;
                client.usrPwd = cmd.Password;
                client.usrDir = cmd.Directory;
                serverDir += "\\user" + client.usrID;
                client.vers = 0;
                SendCommand(stateClient.workSocket, new SyncCommand(SyncCommand.CommandSet.AUTHORIZED));
                statusDelegate("Send Back Authorized Message (NewUser)", fSyncServer.LOG_INFO);
                return true;
            }
        }

        public Boolean StartSession()
        {
            Int64 userID = mySQLite.checkUserDirectory(client.usrNam, cmd.Directory); //Call DB Check Directory User
            if (userID == -1)
            {
                statusDelegate("User Directory Change NOT Authorized (StartSession)", fSyncServer.LOG_INFO);
                SendCommand(stateClient.workSocket, new SyncCommand(SyncCommand.CommandSet.UNAUTHORIZED));
                statusDelegate("Send Back Unauthorized Message because the user change the root directory for the connection (StartSession)", fSyncServer.LOG_INFO);
                return true;
            }
            else
            {
                client.usrID = userID;
                client.usrDir = cmd.Directory;
                Int64 lastVers = 0;
                mySQLite.getUserMinMaxVersion(client.usrID, ref lastVers);
                client.vers = lastVers; //Call DB Get Last Version
                statusDelegate("User Directory Authorized, Start Send Check(StartSession)", fSyncServer.LOG_INFO);
                SendCommand(stateClient.workSocket, new SyncCommand(SyncCommand.CommandSet.AUTHORIZED));
                userChecksum = mySQLite.getUserFiles(client.usrID, client.vers, serverDir); //Call DB Get Users Files

                foreach (FileChecksum check in userChecksum)
                {
                    SendCommand(stateClient.workSocket, new SyncCommand(SyncCommand.CommandSet.CHECK, check.FileNameClient, check.Checksum.ToString()));
                    statusDelegate("Send check Message(StartSession)", fSyncServer.LOG_INFO);
                }
                TEMP = new List<FileChecksum>();
                SendCommand(stateClient.workSocket, new SyncCommand(SyncCommand.CommandSet.ENDCHECK));
                statusDelegate("Send End check Message (StartSession)", fSyncServer.LOG_INFO);
                return true;
            }
        }

        public Boolean GetVersions()
        {
           Int64 lastVers = 0;
           Int64 currentVersion = mySQLite.getUserMinMaxVersion(client.usrID, ref lastVers);
           bool first = true;

            List<FileChecksum> userChecksumA = mySQLite.getUserFiles(client.usrID, currentVersion, serverDir); //Call DB Get Users Files;
            while (currentVersion <= lastVers)
            {
                SendCommand(stateClient.workSocket, new SyncCommand(SyncCommand.CommandSet.VERSION, currentVersion.ToString(), userChecksumA.Count.ToString(),userChecksumA[0].Timestamp ));
                statusDelegate("Send Version Message(Version Command)", fSyncServer.LOG_INFO);

                if (first)
                {
                    foreach (FileChecksum check in userChecksumA)
                    {
                        SendCommand(stateClient.workSocket, new SyncCommand(SyncCommand.CommandSet.CHECKVERSION, check.FileNameClient, "NEW"));
                        statusDelegate("Send check Version Message(Version Command)", fSyncServer.LOG_INFO);
                    }
                    first = false;
                }
                else
                {

                    List<FileChecksum> userChecksumB = mySQLite.getUserFiles(client.usrID, currentVersion, serverDir); //Call DB Get Users Files;

                    foreach (FileChecksum checkB in userChecksumB)
                    {
                        Boolean found = false;

                        foreach (FileChecksum checkA in userChecksumA)
                        {

                            if (checkA.FileNameClient == checkB.FileNameClient)
                                if (checkA.FileNameServer == checkB.FileNameServer)
                                {
                                    SendCommand(stateClient.workSocket, new SyncCommand(SyncCommand.CommandSet.CHECKVERSION, checkA.FileNameClient, "NONE"));
                                    statusDelegate("Send checkVers Message(Version Command)", fSyncServer.LOG_INFO);
                                    found = true;
                                    userChecksumA.Remove(checkA);
                                    break;
                                }
                                else
                                {
                                    SendCommand(stateClient.workSocket, new SyncCommand(SyncCommand.CommandSet.CHECKVERSION, checkA.FileNameClient, "EDIT"));
                                    statusDelegate("Send checkVers Message(Version Command)", fSyncServer.LOG_INFO);
                                    found = true;
                                    userChecksumA.Remove(checkA);
                                    break;
                                }
                        }


                        if (!found)
                        {
                            SendCommand(stateClient.workSocket, new SyncCommand(SyncCommand.CommandSet.CHECKVERSION, checkB.FileNameClient, "NEW"));
                            statusDelegate("Send checkVers Message(Version Command)", fSyncServer.LOG_INFO);
                        }
                    }

                    if (userChecksumA.Count != 0)
                    {
                        foreach (FileChecksum checkA in userChecksumA)
                        {
                            SendCommand(stateClient.workSocket, new SyncCommand(SyncCommand.CommandSet.CHECKVERSION, checkA.FileNameClient, "DEL"));
                            statusDelegate("Send check Message(Version Command)", fSyncServer.LOG_INFO);
                        }
                    }
                    userChecksumA = userChecksumB;
                }
                currentVersion++;

            }
            SendCommand(stateClient.workSocket, new SyncCommand(SyncCommand.CommandSet.ENDCHECK));
            statusDelegate("Send End check Message (Version Command)", fSyncServer.LOG_INFO);
            WellStop();
            return true;
        }



        public Boolean DeleteFile()
        {
            int index = userChecksum.FindIndex(x => x.FileNameClient == cmd.FileName);
            userChecksum.RemoveAt(index);
            statusDelegate("File Correctly Delete from the list of the files of the current Version (DeleteFile)", fSyncServer.LOG_INFO);
            return true; // Da Implementare Meglio
        }

        public Boolean EndSync()
        {

            client.vers++;
            if (userChecksum.Count > 0)
            {
                foreach (FileChecksum check in userChecksum)
                {
                    TEMP.Add(check);
                }
                userChecksum.Clear();
            }
            mySQLite.setUserFiles(client.usrID, client.vers, TEMP); // Call DB Update to new Version all the Files
            TEMP.Clear();
            statusDelegate("DB Updated Correctly (EndSync)", fSyncServer.LOG_INFO);
            CancelVersion();
            WellStop();
            return true;
        }

        public Boolean NoSync()
        {
            WellStop();
            userChecksum.Clear();
            TEMP.Clear();
            CancelVersion();
            return true;
        }

        public Boolean NewFile()
        {
            string fileNameDB = Utility.FilePathWithVers(cmd.FileName, client.vers + 1);
            ReceiveFile(serverDir + fileNameDB, cmd.FileSize);
            statusDelegate("Received New File correcty (NewFile)", fSyncServer.LOG_INFO);
            FileChecksum file = new FileChecksum(cmd.FileName, serverDir + fileNameDB, fileNameDB);
            TEMP.Add(file);
            return true;
        }

        public Boolean EditFile()
        {
            int index = userChecksum.FindIndex(x => x.FileNameClient == cmd.FileName);
            userChecksum.RemoveAt(index);
            statusDelegate("File Correctly Delete from the list of the files of the current Version (EditFile)", fSyncServer.LOG_INFO);
            string fileNameDB = Utility.FilePathWithVers(cmd.FileName, client.vers + 1);
            ReceiveFile(serverDir + fileNameDB, cmd.FileSize);
            statusDelegate("Received File to Edit correcty  (EditFile)", fSyncServer.LOG_INFO);
            FileChecksum file = new FileChecksum(cmd.FileName, serverDir + fileNameDB, fileNameDB);
            TEMP.Add(file);
            return true;
        }

        public Boolean RestoreVersion()
        {
            userChecksum = mySQLite.getUserFiles(client.usrID, cmd.Version, serverDir); //Call DB Retrieve Version to Restore
            foreach (FileChecksum check in userChecksum)
            {
                if (File.Exists(check.FileNameServer))
                {
                    if (RestoreFileClient(check.FileNameServer, check.FileNameClient))
                        statusDelegate("File Sended Succesfully, Server Name:" + check.FileNameServer + "User Name: " + check.FileNameClient + "(Restore Version)", fSyncServer.LOG_INFO);
                    else statusDelegate("Protocol Error Sending File (Restore Version)", fSyncServer.LOG_INFO);
                }
                else
                {
                    statusDelegate("File doesn't exists  " + check.FileNameServer + "(Restore Version)", fSyncServer.LOG_INFO);
                }
            }

            client.vers++;
            mySQLite.setUserFiles(client.usrID, client.vers, userChecksum); // Call DB Update to new Version all the Files
            statusDelegate("Update DB (Restore Command)", fSyncServer.LOG_INFO);

            SendCommand(stateClient.workSocket, new SyncCommand(SyncCommand.CommandSet.ENDRESTORE));
            statusDelegate("Send End Restore Message (Restore Command)", fSyncServer.LOG_INFO);

            WellStop();

            return true;
        }


        public Boolean SendFileClient()
        {
            int index = userChecksum.FindIndex(x => x.FileNameClient == cmd.FileName);
            String fileName = userChecksum[index].FileNameServer;

            if (File.Exists(fileName))
            {
                SendCommand(stateClient.workSocket, new SyncCommand(SyncCommand.CommandSet.FILE, cmd.FileName));
                statusDelegate("Send File Command  ", fSyncServer.LOG_INFO);
                // Send file fileName to remote device
                stateClient.workSocket.SendFile(fileName);
                statusDelegate("File Sended Succesfully", fSyncServer.LOG_INFO);
                // TODO wait for ack
                receiveDone.Reset();
                // Receive the response from the remote device.
                this.ReceiveCommand(stateClient.workSocket);
                receiveDone.WaitOne();
                if (cmd.Type != SyncCommand.CommandSet.ACK)
                    return false;
                return true;
            }
            else
            {
                // todo FILE DOESN'T EXISTS MESSAGGE
                statusDelegate("File doesn't exists  " + fileName, fSyncServer.LOG_INFO);
                return true;
            }

        }

        public Boolean RestoreFileClient(String serverName, String clientName)
        {

            FileInfo fi = new FileInfo(serverName);
            SendCommand(stateClient.workSocket, new SyncCommand(SyncCommand.CommandSet.FILE, clientName, fi.Length.ToString()));
            statusDelegate("Send File Command with Name and Size (Restore Comand)", fSyncServer.LOG_INFO);
            // Send file fileName to remote device
            stateClient.workSocket.SendFile(serverName);
            statusDelegate("File" + serverName + " Sended Succesfully (Restore Comand)", fSyncServer.LOG_INFO);
            receiveDone.Reset();
            // Receive the response from the remote device.
            this.ReceiveCommand(stateClient.workSocket);
            receiveDone.WaitOne();
            if (cmd.Type != SyncCommand.CommandSet.ACK)
                return false;
            return true;

        }

        public void ReceiveFile(String fileName, Int64 fileLength)
        {
            byte[] buffer = new byte[1024];
            int rec = 0;

            if (!Directory.Exists(Path.GetDirectoryName(fileName)))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fileName));
            }
            BinaryWriter bFile = new BinaryWriter(File.Open(fileName, FileMode.Create));

            // Receive data from the server
            while (fileLength > 0)
            {
                rec = stateClient.workSocket.Receive(buffer);
                fileLength -= rec;
                bFile.Write(buffer, 0, rec);
            }
            bFile.Close();
            SendCommand(stateClient.workSocket, new SyncCommand(SyncCommand.CommandSet.ACK));

        }

        public void SendCommand(Socket handler, SyncCommand command)
        {

            try
            {
                if (!syncEnd)
                {
                    // Convert the string data to byte data using ASCII encoding.
                    byte[] byteData = Encoding.ASCII.GetBytes(command.convertToString());

                    statusDelegate("SendCommand Started", fSyncServer.LOG_INFO);

                    // Begin sending the data to the remote device.
                    handler.BeginSend(byteData, 0, byteData.Length, 0,
                        new AsyncCallback(SendCallback), handler);
                }
            }
            catch (Exception e)
            {
                statusDelegate("Exception: " + e.Message, fSyncServer.LOG_INFO);
                if (stopped)
                    statusDelegate("DON'T WORRY SERVER IT'S BEEN STOPPED BY CONNECTION CLOSE, IT'S ALL FINE (Send Command)", fSyncServer.LOG_INFO);
                else
                    StopService();
            }
        }

        public void SendCallback(IAsyncResult ar)
        {
            try
            {
                // Retrieve the socket from the state object.
                // Socket handler = (Socket)ar.AsyncState;

                // Complete sending the data to the remote device.
                if (!syncEnd)
                {
                    int bytesSent = stateClient.workSocket.EndSend(ar);
                }
            }
            catch (Exception e)
            {
                statusDelegate("Exception: " + e.Message, fSyncServer.LOG_INFO);
                if (stopped)
                    statusDelegate("DON'T WORRY SERVER IT'S BEEN STOPPED BY CONNECTION CLOSE, IT'S ALL FINE (Send Callback)", fSyncServer.LOG_INFO);
                else
                    StopService();
            }
        }

        public Boolean SocketConnected(Socket s)
        {
            bool part1 = s.Poll(1000, SelectMode.SelectRead);
            bool part2 = (s.Available == 0);
            if (part1 && part2)
                return false;
            else
                return true;
        }

        public Boolean CancelVersion()
        {

            Int64 maxVers = 0;
            Int64 minVers = mySQLite.getUserMinMaxVersion(client.usrID, ref maxVers);
            Int64 diff = maxVers - minVers;
            while (diff > maxVersionNumber)
            {
                userChecksum = mySQLite.getUserFiles(client.usrID, minVers, serverDir); //Call DB Get Users Files;
                minVers++;
                TEMP = mySQLite.getUserFiles(client.usrID, minVers, serverDir); //Call DB Get Users Files;
                foreach (FileChecksum check in userChecksum)
                {

                    int index = TEMP.FindIndex(x => x.FileNameServer == check.FileNameServer);

                    if (index == -1)
                    {
                        File.Delete(check.FileNameServer);
                        statusDelegate("Deleted File Correctly:" + check.FileNameServer, fSyncServer.LOG_INFO);
                    }

                }
                mySQLite.deleteVersion(client.usrID, minVers - 1);
                diff--;

            }
            userChecksum.Clear();
            TEMP.Clear();
            return true;
        }



    }
}
