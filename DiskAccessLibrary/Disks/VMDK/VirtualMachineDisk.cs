/* Copyright (C) 2014 Tal Aloni <tal.aloni.il@gmail.com>. All rights reserved.
 * 
 * You can redistribute this program and/or modify it under the terms of
 * the GNU Lesser Public License as published by the Free Software Foundation,
 * either version 3 of the License, or (at your option) any later version.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Utilities;
using DiskAccessLibrary.VMDK;

namespace DiskAccessLibrary
{
    public partial class VirtualMachineDisk : DiskImage, IDiskGeometry
    {
        private const uint BaseDiskParentCID = 0xffffffff;

        private string m_descriptorPath;
        private VirtualMachineDiskDescriptor m_descriptor;

        private DiskImage m_extent;

        public VirtualMachineDisk(string descriptorPath) : base(descriptorPath)
        {
            m_descriptorPath = descriptorPath;

            m_descriptor = VirtualMachineDiskDescriptor.ReadFromFile(m_descriptorPath);
            bool isDescriptorEmbedded = false;
            if (m_descriptor == null)
            {
                SparseExtent sparse = new SparseExtent(m_descriptorPath);
                if (sparse.Descriptor != null)
                {
                    isDescriptorEmbedded = true;
                    m_descriptor = sparse.Descriptor;
                    m_extent = sparse;
                }
                else
                {
                    throw new InvalidDataException("Missing VMDK descriptor");
                }
            }

            if (m_descriptor.Version != 1)
            {
                throw new NotImplementedException("Unsupported VMDK descriptor version");
            }

            if (m_descriptor.ParentContentID != BaseDiskParentCID)
            {
                throw new InvalidDataException("VMDK descriptor ParentContentID does not match BaseDiskParentCID");
            }

            if (!isDescriptorEmbedded && m_descriptor.DiskType != VirtualMachineDiskType.MonolithicFlat)
            {
                throw new NotImplementedException("Unsupported VMDK disk type");
            }

            if (isDescriptorEmbedded && m_descriptor.DiskType != VirtualMachineDiskType.MonolithicSparse)
            {
                throw new NotImplementedException("Unsupported VMDK disk type");
            }

            foreach (VirtualMachineDiskExtentEntry extentEntry in m_descriptor.ExtentEntries)
            {
                if (!isDescriptorEmbedded && extentEntry.ExtentType != ExtentType.Flat)
                {
                    throw new NotImplementedException("Unsupported VMDK extent type");
                }

                if (isDescriptorEmbedded && extentEntry.ExtentType != ExtentType.Sparse)
                {
                    throw new NotImplementedException("Unsupported VMDK extent type");
                }
            }

            if (m_descriptor.ExtentEntries.Count != 1)
            {
                throw new NotImplementedException("Unsupported number of VMDK extents");
            }

            if (m_descriptor.DiskType == VirtualMachineDiskType.MonolithicFlat)
            {
                VirtualMachineDiskExtentEntry entry = m_descriptor.ExtentEntries[0];
                string directory = System.IO.Path.GetDirectoryName(descriptorPath);
                DiskImage extent = new RawDiskImage(directory + @"\" + entry.FileName);
                m_extent = extent;
            }
        }

        public override bool ExclusiveLock()
        {
            return m_extent.ExclusiveLock();
        }

        public override bool ReleaseLock()
        {
            return m_extent.ReleaseLock();
        }

        public override byte[] ReadSectors(long sectorIndex, int sectorCount)
        {
            return m_extent.ReadSectors(sectorIndex, sectorCount);
        }

        public override void WriteSectors(long sectorIndex, byte[] data)
        {
            if (IsReadOnly)
            {
                throw new UnauthorizedAccessException("Attempted to perform write on a readonly disk");
            }
            m_extent.WriteSectors(sectorIndex, data);
        }

        public override void Extend(long additionalNumberOfBytes)
        {
            if (m_descriptor.DiskType == VirtualMachineDiskType.MonolithicFlat)
            {
                // Add updated extent entries
                List<string> lines = VirtualMachineDiskDescriptor.ReadASCIITextLines(m_descriptorPath);
                m_descriptor.ExtentEntries[0].SizeInSectors += additionalNumberOfBytes / this.BytesPerSector;
                m_descriptor.UpdateExtentEntries(lines);

                File.WriteAllLines(m_descriptorPath, lines.ToArray(), Encoding.ASCII);
                ((DiskImage)m_extent).Extend(additionalNumberOfBytes);
            }
            else
            {
                throw new NotImplementedException("Extending a monolithic sparse is not supported");
            }
        }

        public override int BytesPerSector
        {
            get
            {
                return DiskImage.BytesPerDiskImageSector;
            }
        }

        public override long Size
        {
            get
            {
                return m_extent.Size;
            }
        }

        public long Cylinders
        {
            get
            {
                return m_descriptor.Cylinders;
            }
        }

        public int TracksPerCylinder
        {
            get
            {
                return m_descriptor.TracksPerCylinder;
            }
        }

        public int SectorsPerTrack
        {
            get
            {
                return m_descriptor.SectorsPerTrack;
            }
        }
    }
}
