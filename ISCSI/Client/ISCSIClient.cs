/* Copyright (C) 2012-2016 Tal Aloni <tal.aloni.il@gmail.com>. All rights reserved.
 * 
 * You can redistribute this program and/or modify it under the terms of
 * the GNU Lesser Public License as published by the Free Software Foundation,
 * either version 3 of the License, or (at your option) any later version.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Utilities;

namespace ISCSI.Client
{
    public class ISCSIClient
    {
        // Offered Session Parameters:
        public static bool OfferedInitialR2T = true;
        public static bool OfferedImmediateData = true;
        public static int OfferedMaxBurstLength = SessionParameters.DefaultMaxBurstLength;
        public static int OfferedFirstBurstLength = SessionParameters.DefaultFirstBurstLength;
        public static int OfferedDefaultTime2Wait = 0;
        public static int OfferedDefaultTime2Retain = 20;
        public static int OfferedMaxOutstandingR2T = 1;
        public static bool OfferedDataPDUInOrder = true;
        public static bool OfferedDataSequenceInOrder = true;
        public static int OfferedErrorRecoveryLevel = 0;
        public static int OfferedMaxConnections = 1;

        private SessionParameters m_session = new SessionParameters();
        private ConnectionParameters m_connection = new ConnectionParameters();

        private string m_initiatorName;
        private IPAddress m_targetAddress;
        private int m_targetPort;
        private bool m_isConnected;
        private Socket m_clientSocket;
        
        private object m_incomingQueueLock = new object();
        private List<ISCSIPDU> m_incomingQueue = new List<ISCSIPDU>();
        private IAsyncResult m_currentAsyncResult;

        public static object m_logSyncLock = new object();
        private static FileStream m_logFile = null;

        public ISCSIClient(string initiatorName)
        {
            m_initiatorName = initiatorName;
        }

        public bool Connect(IPAddress targetAddress, int targetPort)
        {
            m_targetAddress = targetAddress;
            m_targetPort = targetPort;
            if (!m_isConnected)
            {
                m_clientSocket = new Socket(m_targetAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    m_clientSocket.Connect(m_targetAddress, m_targetPort);
                }
                catch (SocketException)
                {
                    return false;
                }
                StateObject state = new StateObject();
                m_currentAsyncResult = m_clientSocket.BeginReceive(state.ReceiveBuffer, 0, state.ReceiveBuffer.Length, SocketFlags.None, new AsyncCallback(OnClientSocketReceive), state);
                m_isConnected = true;
            }
            return m_isConnected;
        }

        public void Disconnect()
        {
            if (m_isConnected)
            {
                m_clientSocket.Disconnect(false);
                m_isConnected = false;
            }
        }

        /// <param name="targetName">Set to null for discovery session</param>
        public bool Login(string targetName)
        {
            if (!m_isConnected)
            {
                throw new InvalidOperationException("iSCSI client is not connected");
            }
            m_session.ISID = ClientHelper.GetRandomISID();
            m_connection.CID = m_session.GetNextCID();
            // p.s. It's possible to perform a single stage login (stage 1 to stage 3, tested against Microsoft iSCSI Target v3.1)
            LoginRequestPDU request = ClientHelper.GetFirstStageLoginRequest(m_initiatorName, targetName, m_session, m_connection);
            SendPDU(request);
            LoginResponsePDU response = WaitForPDU(request.InitiatorTaskTag) as LoginResponsePDU;
            if (response != null && response.Status == LoginResponseStatusName.Success)
            {
                // Status numbering starts with the Login response to the first Login request of the connection
                m_connection.StatusNumberingStarted = true;
                m_connection.ExpStatSN = response.StatSN + 1;

                request = ClientHelper.GetSecondStageLoginRequest(response, m_session, m_connection, targetName == null);
                SendPDU(request);
                response = WaitForPDU(request.InitiatorTaskTag) as LoginResponsePDU;
                if (response != null && response.Status == LoginResponseStatusName.Success)
                {
                    ClientHelper.UpdateOperationalParameters(response.LoginParameters, m_session, m_connection);
                    return true;
                }
            }
            return false;
        }

        public bool Logout()
        {
            if (!m_isConnected)
            {
                throw new InvalidOperationException("iSCSI client is not connected");
            }
            LogoutRequestPDU request = ClientHelper.GetLogoutRequest(m_session, m_connection);
            SendPDU(request);
            LogoutResponsePDU response = WaitForPDU(request.InitiatorTaskTag) as LogoutResponsePDU;
            return (response != null && response.Response == LogoutResponse.ClosedSuccessfully);
        }

        public List<string> ListTargets()
        {
            if (!m_isConnected)
            {
                throw new InvalidOperationException("iSCSI client is not connected");
            }
            TextRequestPDU request = ClientHelper.GetSendTargetsRequest(m_session, m_connection);
            SendPDU(request);
            TextResponsePDU response = WaitForPDU(request.InitiatorTaskTag) as TextResponsePDU;
            if (response != null && response.Final)
            {
                KeyValuePairList<string, string> entries = KeyValuePairUtils.GetKeyValuePairList(response.Text);
                List<string> result = new List<string>();
                foreach(KeyValuePair<string, string> entry in entries)
                {
                    if (entry.Key == "TargetName")
                    {
                        result.Add(entry.Value);
                    }
                }
                return result;
            }
            return null;
        }

        public List<ushort> GetLUNList()
        {
            if (!m_isConnected)
            {
                throw new InvalidOperationException("iSCSI client is not connected");
            }
            SCSICommandPDU reportLUNs = ClientHelper.GetReportLUNsCommand(m_session, m_connection, ReportLUNsParameter.MinimumAllocationLength);
            SendPDU(reportLUNs);
            SCSIDataInPDU data = WaitForPDU(reportLUNs.InitiatorTaskTag) as SCSIDataInPDU;
            if (data != null && data.StatusPresent && data.Status == SCSIStatusCodeName.Good)
            {
                uint requiredAllocationLength = ReportLUNsParameter.GetRequiredAllocationLength(data.Data);
                if (requiredAllocationLength > ReportLUNsParameter.MinimumAllocationLength)
                {
                    reportLUNs = ClientHelper.GetReportLUNsCommand(m_session, m_connection, requiredAllocationLength);
                    m_clientSocket.Send(reportLUNs.GetBytes());
                    data = WaitForPDU(reportLUNs.InitiatorTaskTag) as SCSIDataInPDU;

                    if (data == null || !data.StatusPresent || data.Status != SCSIStatusCodeName.Good)
                    {
                        return null;
                    }
                }

                ReportLUNsParameter parameter = new ReportLUNsParameter(data.Data);
                List<ushort> result = new List<ushort>();
                foreach(LUNStructure lun in parameter.LUNList)
                {
                    if (lun.IsSingleLevelLUN)
                    {
                        result.Add(lun);
                    }
                }
                return result;
                
            }
            return null;
        }

        /// <returns>Capacity in bytes</returns>
        public ulong ReadCapacity(ushort LUN, out int bytesPerSector)
        {
            if (!m_isConnected)
            {
                throw new InvalidOperationException("iSCSI client is not connected");
            }
            SCSICommandPDU readCapacity = ClientHelper.GetReadCapacity10Command(m_session, m_connection, LUN);
            SendPDU(readCapacity);
            // SCSIResponsePDU with CheckCondition could be returned in case of an error
            SCSIDataInPDU data = WaitForPDU(readCapacity.InitiatorTaskTag) as SCSIDataInPDU;
            if (data != null && data.StatusPresent && data.Status == SCSIStatusCodeName.Good)
            {
                ReadCapacity10Parameter capacity = new ReadCapacity10Parameter(data.Data);
                if (capacity.ReturnedLBA != 0xFFFFFFFF)
                {
                    bytesPerSector = (int)capacity.BlockLengthInBytes;
                    return (capacity.ReturnedLBA + 1) * capacity.BlockLengthInBytes;
                }

                readCapacity = ClientHelper.GetReadCapacity16Command(m_session, m_connection, LUN);
                m_clientSocket.Send(readCapacity.GetBytes());
                data = WaitForPDU(readCapacity.InitiatorTaskTag) as SCSIDataInPDU;
                if (data != null && data.StatusPresent && data.Status == SCSIStatusCodeName.Good)
                {
                    ReadCapacity16Parameter capacity16 = new ReadCapacity16Parameter(data.Data);
                    bytesPerSector = (int)capacity16.BlockLengthInBytes;
                    return (capacity16.ReturnedLBA + 1) * capacity16.BlockLengthInBytes;
                }
            }

            bytesPerSector = 0;
            return 0;
        }

        public byte[] Read(ushort LUN, ulong sectorIndex, uint sectorCount, int bytesPerSector)
        {
            if (!m_isConnected)
            {
                throw new InvalidOperationException("iSCSI client is not connected");
            }
            SCSICommandPDU readCommand = ClientHelper.GetRead16Command(m_session, m_connection, LUN, sectorIndex, sectorCount, bytesPerSector);
            SendPDU(readCommand);
            // RFC 3720: Data payload is associated with a specific SCSI command through the Initiator Task Tag
            SCSIDataInPDU data = WaitForPDU(readCommand.InitiatorTaskTag) as SCSIDataInPDU;
            byte[] result = new byte[sectorCount * bytesPerSector];
            while (data != null)
            {
                Array.Copy(data.Data, 0, result, data.BufferOffset, data.DataSegmentLength);
                if (data.StatusPresent)
                {
                    break;
                }
                data = WaitForPDU(readCommand.InitiatorTaskTag) as SCSIDataInPDU;
            }
            if (data != null && data.Status == SCSIStatusCodeName.Good)
            {
                return result;
            }
            else
            {
                return null;
            }
        }

        public bool Write(ushort LUN, ulong sectorIndex, byte[] data, int bytesPerSector)
        {
            if (!m_isConnected)
            {
                throw new InvalidOperationException("iSCSI client is not connected");
            }
            SCSICommandPDU writeCommand = ClientHelper.GetWrite16Command(m_session, m_connection, LUN, sectorIndex, data, bytesPerSector);
            SendPDU(writeCommand);
            ISCSIPDU response = WaitForPDU(writeCommand.InitiatorTaskTag);
            while (response is ReadyToTransferPDU)
            {
                List<SCSIDataOutPDU> requestedData = ClientHelper.GetWriteData(m_session, m_connection, LUN, sectorIndex, data, bytesPerSector, (ReadyToTransferPDU)response);
                foreach (SCSIDataOutPDU dataOut in requestedData)
                {
                    SendPDU(dataOut);
                }
                response = WaitForPDU(writeCommand.InitiatorTaskTag);
            }

            if (response is SCSIResponsePDU)
            {
                if (((SCSIResponsePDU)response).Status == SCSIStatusCodeName.Good)
                {
                    return true;
                }
            }
            return false;
        }

        public bool PingTarget()
        {
            if (!m_isConnected)
            {
                throw new InvalidOperationException("iSCSI client is not connected");
            }
            NOPOutPDU request = ClientHelper.GetPingRequest(m_session, m_connection);
            SendPDU(request);
            NOPInPDU response = WaitForPDU(request.InitiatorTaskTag) as NOPInPDU;
            return response != null;
        }

        private void OnClientSocketReceive(IAsyncResult ar)
        {
            if (ar != m_currentAsyncResult)
            {
                // We ignore calls for old sockets which we no longer use
                // See: http://rajputyh.blogspot.co.il/2010/04/solve-exception-message-iasyncresult.html
                return;
            }

            StateObject state = (StateObject)ar.AsyncState;

            if (!m_clientSocket.Connected)
            {
                return;
            }

            int numberOfBytesReceived = 0;
            try
            {
                numberOfBytesReceived = m_clientSocket.EndReceive(ar);
            }
            catch (ObjectDisposedException)
            {
                Log("[ReceiveCallback] EndReceive ObjectDisposedException");
                return;
            }
            catch (SocketException ex)
            {
                Log("[ReceiveCallback] EndReceive SocketException: " + ex.Message);
                return;
            }

            if (numberOfBytesReceived == 0)
            {
                m_isConnected = false;
            }
            else
            {
                byte[] currentBuffer = ByteReader.ReadBytes(state.ReceiveBuffer, 0, numberOfBytesReceived);
                ProcessCurrentBuffer(currentBuffer, state);

                try
                {
                    m_currentAsyncResult = m_clientSocket.BeginReceive(state.ReceiveBuffer, 0, state.ReceiveBuffer.Length, SocketFlags.None, new AsyncCallback(OnClientSocketReceive), state);
                }
                catch (ObjectDisposedException)
                {
                    m_isConnected = false;
                    Log("[ReceiveCallback] BeginReceive ObjectDisposedException");
                }
                catch (SocketException ex)
                {
                    m_isConnected = false;
                    Log("[ReceiveCallback] BeginReceive SocketException: " + ex.Message);
                }
            }
        }

        public void ProcessCurrentBuffer(byte[] currentBuffer, StateObject state)
        {
            if (state.ConnectionBuffer.Length == 0)
            {
                state.ConnectionBuffer = currentBuffer;
            }
            else
            {
                state.ConnectionBuffer = ByteUtils.Concatenate(state.ConnectionBuffer, currentBuffer);
            }

            // we now have all PDU bytes received so far in state.ConnectionBuffer
            int bytesLeftInBuffer = state.ConnectionBuffer.Length;

            while (bytesLeftInBuffer >= 8)
            {
                int bufferOffset = state.ConnectionBuffer.Length - bytesLeftInBuffer;
                int pduLength = ISCSIPDU.GetPDULength(state.ConnectionBuffer, bufferOffset);
                if (pduLength > bytesLeftInBuffer)
                {
                    break;
                }
                else
                {
                    byte[] pduBytes = ByteReader.ReadBytes(state.ConnectionBuffer, bufferOffset, pduLength);
                    bytesLeftInBuffer -= pduLength;
                    ISCSIPDU pdu = null;
                    try
                    {
                        pdu = ISCSIPDU.GetPDU(pduBytes);
                    }
                    catch (UnsupportedSCSICommandException)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        throw;
                    }

                    if (pdu.GetType() == typeof(ISCSIPDU))
                    {
                        /*
                        Log("[{0}][ProcessCurrentBuffer] Unsupported PDU (0x{1})", state.Connection.Identifier, pdu.OpCode.ToString("X"));
                        // Unsupported PDU
                        RejectPDU reject = new RejectPDU();
                        reject.Reason = RejectReason.CommandNotSupported;
                        reject.StatSN = state.Connection.StatSN;
                        reject.ExpCmdSN = state.Connection.ExpCmdSN;
                        reject.MaxCmdSN = state.Connection.ExpCmdSN + ISCSIServer.CommandQueueSize;
                        reject.Data = ByteReader.ReadBytes(pduBytes, 0, 48);

                        // StatSN is advanced after a Reject
                        state.Connection.StatSN++;

                        TrySendPDU(state, reject);*/
                        throw new Exception("Unsupported");
                    }
                    else
                    {
                        ProcessPDU(pdu, state);
                    }
                }
            }

            if (bytesLeftInBuffer > 0)
            {
                state.ConnectionBuffer = ByteReader.ReadBytes(state.ConnectionBuffer, state.ConnectionBuffer.Length - bytesLeftInBuffer, bytesLeftInBuffer);
            }
            else
            {
                state.ConnectionBuffer = new byte[0];
            }
        }

        public void ProcessPDU(ISCSIPDU pdu, StateObject state)
        {
            if (pdu is NOPInPDU)
            {
                if (((NOPInPDU)pdu).TargetTransferTag != 0xFFFFFFFF)
                {
                    // Send NOP-OUT
                    NOPOutPDU response = ClientHelper.GetPingResponse((NOPInPDU)pdu, m_session, m_connection);
                    SendPDU(response);
                    return;
                }
            }
            
            if (m_connection.StatusNumberingStarted)
            {
                uint? responseStatSN = PDUHelper.GetStatSN(pdu);
                if (m_connection.ExpStatSN == responseStatSN)
                {
                    m_connection.ExpStatSN++;
                }
            }
            lock (m_incomingQueueLock)
            {
                m_incomingQueue.Add(pdu);
            }
        }

        public ISCSIPDU WaitForPDU(uint initiatorTaskTag)
        {
            const int TimeOut = 5000;
            int timespan = 0;
            while (timespan < TimeOut)
            {
                lock (m_incomingQueueLock)
                {
                    for (int index = 0; index < m_incomingQueue.Count; index++)
                    {
                        ISCSIPDU pdu = m_incomingQueue[index];
                        if (pdu.InitiatorTaskTag == initiatorTaskTag)
                        {
                            m_incomingQueue.RemoveAt(index);
                            return pdu;
                        }
                    }
                }
                Thread.Sleep(100);
                timespan += 100;
            }
            return null;
        }

        public void SendPDU(ISCSIPDU request)
        {
            try
            {
                if (m_connection.StatusNumberingStarted)
                {
                    PDUHelper.SetExpStatSN(request, m_connection.ExpStatSN);
                }
                m_clientSocket.Send(request.GetBytes());
                Log("[{0}][SendPDU] Sent request to target, Operation: {1}, Size: {2}", this.ConnectionIdentifier, (ISCSIOpCodeName)request.OpCode, request.Length);
            }
            catch (SocketException ex)
            {
                Log("[{0}][SendPDU] Failed to send PDU to target (Operation: {1}, Size: {2}), SocketException: {3}", this.ConnectionIdentifier, (ISCSIOpCodeName)request.OpCode, request.Length, ex.Message);
                m_isConnected = false;
            }
            catch (ObjectDisposedException)
            {
                m_isConnected = false;
            }
        }

        public string ConnectionIdentifier
        {
            get
            {
                return String.Format("ISID={0},TSIH={1},CID={2}", m_session.ISID.ToString("x"), m_session.TSIH.ToString("x"), m_connection.CID.ToString("x"));
            }
        }

        public bool IsConnected
        {
            get
            {
                return m_isConnected;
            }
        }

        public static void Log(string message, params object[] args)
        {
            Log(String.Format(message, args));
        }

        public static void Log(string message)
        {
            if (m_logFile != null)
            {
                lock (m_logSyncLock)
                {
                    StreamWriter writer = new StreamWriter(m_logFile);
                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ");
                    writer.WriteLine(timestamp + message);
                    writer.Flush();
                }
            }
        }
    }
}
