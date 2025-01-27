﻿using NetTopologySuite.Geometries;
using NetTopologySuite.IO.Esri.Shapefiles.Readers;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace NetTopologySuite.IO.Esri.Shp.Readers
{

    /// <summary>
    /// Base class class for reading a fixed-length file header and variable-length records from a *.SHP file. 
    /// </summary>
    public abstract class ShpReader<T> : ShpReader where T : Geometry
    {
        private readonly Stream ShpStream;
        private readonly int ShpEndPosition;
        private readonly MemoryStream Buffer;
        private protected readonly GeometryBuilderMode GeometryBuilderMode;
        private readonly Envelope MbrEnvelope = null;
        private readonly MbrFilterOption MbrFilterOption;
        private readonly Geometry MbrGeometry = null;

        /// <summary>
        /// Shapefile Spec: <br/>
        /// The one-to-one relationship between geometry and attributes is based on record number.
        /// Attribute records in the dBASE file must be in the same order as records in the main file.
        /// </summary>
        /// <remarks>
        /// DBF does not have recor number attribute.
        /// </remarks>
        private int RecordNumber = 1;
        private readonly int DbfRecordCount;

        internal GeometryFactory Factory { get; }

        private readonly Envelope _boundingBox;

        /// <inheritdoc/>
        public override Envelope BoundingBox => _boundingBox.Copy(); // Envelope is not immutable


        private readonly List<Exception> _errors = new List<Exception>();
        /// <summary>
        /// Errors which occured during reading process. Valid only if ShapefileReaderOptions.SkipFailures option is set to true.
        /// </summary>
        public IReadOnlyList<Exception> Errors => _errors;

        /// <summary>
        /// SHP geometry.
        /// </summary>
        public T Shape { get; private set; }

        /// <inheritdoc/>
        public override Geometry Geometry => Shape;

        /// <summary>
        /// Initializes a new instance of the reader class.
        /// </summary>
        /// <param name="shpStream">SHP file stream.</param>
        /// <param name="options">Reader options.</param>
        internal ShpReader(Stream shpStream, ShapefileReaderOptions options)
            : base(Shapefile.GetShapeType(shpStream))
        {
            ShpStream = shpStream ?? throw new ArgumentNullException("Uninitialized SHP stream.", nameof(shpStream));
            Factory = options?.Factory ?? Geometry.DefaultFactory;

            if (options?.MbrFilter?.IsNull == false)
            {
                MbrEnvelope = options.MbrFilter.Copy(); // Envelope is not immutable
            }
            if (MbrEnvelope != null)
            {
                MbrGeometry = Factory.ToGeometry(MbrEnvelope);
            }
            MbrFilterOption = options?.MbrFilterOption ?? MbrFilterOption.FilterByExtent;

            GeometryBuilderMode = options?.GeometryBuilderMode ?? GeometryBuilderMode.Strict;

            DbfRecordCount = options?.DbfRecordCount ?? int.MaxValue;
            if (DbfRecordCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(DbfRecordCount));
            }

            if (ShpStream.Position != 0)
                ShpStream.Seek(0, SeekOrigin.Begin);

            Buffer = new MemoryStream();
            AddManagedResource(Buffer);

            Buffer.AssignFrom(ShpStream, Shapefile.FileHeaderSize);
            Buffer.ReadShpFileHeader(out _, out var fileLength, out _boundingBox);
            ShpEndPosition = fileLength - 1;
        }

        internal override void Restart()
        {
            ShpStream.Seek(Shapefile.FileHeaderSize, SeekOrigin.Begin);
            RecordNumber = 1;
        }

        /// <inheritdoc/>
        public override bool Read()
        {
            return Read(out var _);
        }

        /// <inheritdoc/>
        internal bool Read(out int skippedCount)
        {
            skippedCount = 0;
            return ReadCore(ref skippedCount);
        }

        /// <inheritdoc/>
        internal bool ReadCore(ref int skippedCount)
        {
            if (ShpStream.Position >= ShpEndPosition)
            {
                Shape = null;
                return false;
            }
            if (RecordNumber > DbfRecordCount)
            {
                Shape = null;
                return false;
            }

            (var recordNumber, var contentLength) = ShpStream.ReadShpRecordHeader();
            Debug.Assert(recordNumber == RecordNumber, "Shapefile record", $"Unexpected SHP record number: {recordNumber} (expected {RecordNumber}).");
            Debug.Assert(contentLength >= sizeof(int), "Shapefile record", $"Unexpected SHP record content size: {contentLength} (expected >= {sizeof(int)}).");

            Buffer.AssignFrom(ShpStream, contentLength);
            RecordNumber++;

            var type = Buffer.ReadShapeType();
            if (type == ShapeType.NullShape)
            {
                Shape = GetEmptyGeometry();
                return true;
            }
            else if (type != ShapeType)
            {
                OnInvalidRecordType(type);
                skippedCount++;
                return ReadCore(ref skippedCount);
            }

            try
            {
                if (!ReadGeometry(Buffer, out var geometry))
                {
                    skippedCount++;
                    return ReadCore(ref skippedCount); 
                }
                Shape = geometry;
                return true;
            }
            catch (Exception ex)
            {
                if (GeometryBuilderMode != GeometryBuilderMode.SkipInvalidShapes)
                {
                    throw;
                }
                _errors.Add(ex);
                skippedCount++;
                return ReadCore(ref skippedCount);
            }
        }

        internal bool IsInMbr(Envelope boundingBox)
        {
            if (MbrEnvelope == null)
            {
                return true;
            }
            return MbrEnvelope.Intersects(boundingBox);
        }

        internal bool IsInMbr(Geometry geometry)
        {
            if (MbrGeometry == null || MbrFilterOption != MbrFilterOption.FilterByGeometry)
            {
                return true;
            }
            return MbrGeometry.Intersects(geometry);
        }

        internal abstract T GetEmptyGeometry();

        internal abstract bool ReadGeometry(Stream stream, out T geometry);

        internal CoordinateSequence CreateCoordinateSequence(int size)
        {
            return Factory.CoordinateSequenceFactory.Create(size, HasZ, HasM);
        }

        private void OnReaderError(string message)
        {
            var ex = new ShapefileException(message);
            _errors.Add(ex);

        }

        private void OnInvalidRecordType(ShapeType shapeType)
        {
            OnReaderError($"Ivalid shapefile record type (FID={RecordNumber}). {GetType().Name} does not support {shapeType} shape type.");
        }

        internal void OnInvalidGeometry(string message)
        {
            OnReaderError($"Ivalid shapefile record geometry (FID={RecordNumber}). {message}");
        }
    }


}
