/* Copyright (C) 2012-2016 Tal Aloni <tal.aloni.il@gmail.com>. All rights reserved.
 * 
 * You can redistribute this program and/or modify it under the terms of
 * the GNU Lesser Public License as published by the Free Software Foundation,
 * either version 3 of the License, or (at your option) any later version.
 */
using System;
using System.Collections.Generic;
using System.Text;
using Utilities;

namespace ISCSI.Server
{
    public class ConnectionParameters
    {
        /// <summary>
        /// The default MaxRecvDataSegmentLength is used during Login
        /// </summary>
        public const int DefaultMaxRecvDataSegmentLength = 8192;
        public static int DeclaredMaxRecvDataSegmentLength = 262144;

        public ushort CID; // connection ID, generated by the initiator

        /// <summary>
        /// per direction parameter that the target or initator declares.
        /// maximum data segment length that the target (or initator) can receive in a single iSCSI PDU.
        /// </summary>
        public int InitiatorMaxRecvDataSegmentLength = DefaultMaxRecvDataSegmentLength;
        public int TargetMaxRecvDataSegmentLength = DeclaredMaxRecvDataSegmentLength;

        public uint StatSN = 0; // Initial StatSN, any number will do
        // Dictionary of current transfers: <transfer-tag, <command-bytes, length>>
        // offset - logical block address (sector)
        // length - data transfer length in bytes
        // Note: here incoming means data write operations to the target
        public Dictionary<uint, KeyValuePair<byte[], uint>> Transfers = new Dictionary<uint, KeyValuePair<byte[], uint>>();

        // Dictionary of transfer data: <transfer-tag, command-data>
        public Dictionary<uint, byte[]> TransferData = new Dictionary<uint, byte[]>();
    }
}
