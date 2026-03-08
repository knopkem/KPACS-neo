// ------------------------------------------------------------------------------------------------
// KPACS.DCMClasses - DicomHeader.cs
// Ported from DCMHeaderClass.pas (TdcmHdrObj)
//
// This is the central DICOM dataset class. The Delphi original implemented its own
// binary DICOM parser and stored tags in sorted TStringList structures. This C# port
// uses fo-dicom's DicomDataset as the underlying storage, providing the same public API
// surface while leveraging fo-dicom for parsing, encoding, and serialization.
// ------------------------------------------------------------------------------------------------

using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.IO.Buffer;

namespace KPACS.DCMClasses;

/// <summary>
/// Core DICOM header/dataset object. Wraps fo-dicom's DicomDataset and provides
/// tag navigation, read/write, sequence support, and file I/O.
/// Ported from TdcmHdrObj in DCMHeaderClass.pas.
/// </summary>
public class DicomHeaderObject : DicomBaseObject, IDisposable
{
    private DicomDataset _dataset;
    private DicomFile? _dicomFile;
    private string _fileName = string.Empty;
    private readonly List<DicomHeaderObject> _items = [];
    private int _itemIndex = -1;
    private bool _disposed;

    // Tag navigation state (mirrors the Delphi GotoFirstTag/GotoNextTag pattern)
    private readonly List<DicomItem> _flatTagList = [];
    private int _currentTagIndex = -1;

    // Sequence properties (when this object IS a sequence)
    private bool _isSequence;
    private bool _isSequenceItem;
    private string _sequenceName = string.Empty;
    private ushort _sequenceGroup;
    private ushort _sequenceElement;
    private string _sequenceValue = string.Empty;
    private int _parentItemIndex;

    /// <summary>
    /// Creates a new empty DicomHeaderObject.
    /// </summary>
    public DicomHeaderObject()
    {
        _dataset = new DicomDataset();
    }

    /// <summary>
    /// Creates a DicomHeaderObject from a DICOM file.
    /// </summary>
    /// <param name="fileName">Path to the DICOM file to load.</param>
    public DicomHeaderObject(string fileName) : this()
    {
        if (!string.IsNullOrEmpty(fileName))
            FileName = fileName;
    }

    /// <summary>
    /// Creates a DicomHeaderObject wrapping an existing fo-dicom DicomDataset.
    /// </summary>
    public DicomHeaderObject(DicomDataset dataset) : this()
    {
        _dataset = dataset ?? new DicomDataset();
    }

    // ==============================================================================================
    // Properties
    // ==============================================================================================

    /// <summary>
    /// Gets or sets the file name. Setting it loads the DICOM header from disk.
    /// </summary>
    public string FileName
    {
        get => _fileName;
        set
        {
            var path = value;
            if (!File.Exists(path))
            {
                // Try without extension
                var noExt = Path.ChangeExtension(path, null);
                if (File.Exists(noExt))
                    path = noExt;
                else
                {
                    // Try stripping frame reference (#FRM...)
                    var frmIdx = path.IndexOf("#FRM", StringComparison.Ordinal);
                    if (frmIdx > 0)
                    {
                        var stripped = path[..frmIdx];
                        if (File.Exists(stripped))
                            path = stripped;
                        else
                        {
                            Clear();
                            _fileName = string.Empty;
                            return;
                        }
                    }
                    else
                    {
                        Clear();
                        _fileName = string.Empty;
                        return;
                    }
                }
            }

            ReadDcmHeader(path);
            _fileName = path;
        }
    }

    /// <summary>
    /// Whether to write the 128-byte preamble + "DICM" marker when saving.
    /// </summary>
    public bool WriteWithPreamble { get; set; }

    /// <summary>
    /// Whether to force a file reload even if already loaded.
    /// </summary>
    public bool ForceReload { get; set; }

    /// <summary>
    /// Whether to read pixel data when loading the header.
    /// </summary>
    public bool ReadPixelData { get; set; } = true;

    /// <summary>
    /// Whether to read only the first slice for multiframe images.
    /// </summary>
    public bool MultiFrameReadFirstSliceOnly { get; set; }

    /// <summary>
    /// Number of DICOM groups in the dataset.
    /// </summary>
    public int GroupCount => _dataset.Count() > 0
        ? _dataset.Select(t => t.Tag.Group).Distinct().Count()
        : 0;

    /// <summary>
    /// Total number of tags in the dataset.
    /// </summary>
    public int TagCount => _dataset.Count();

    /// <summary>
    /// Whether this object is a sequence.
    /// </summary>
    public bool IsSequence
    {
        get => _isSequence;
        set => _isSequence = value;
    }

    /// <summary>
    /// Whether this object is a sequence item.
    /// </summary>
    public bool IsSequenceItem
    {
        get => _isSequenceItem;
        set => _isSequenceItem = value;
    }

    /// <summary>
    /// Whether this sequence has items.
    /// </summary>
    public bool HasItems => _items.Count > 0;

    /// <summary>
    /// Number of items in this sequence.
    /// </summary>
    public int ItemsCount => _items.Count;

    /// <summary>
    /// Whether the dataset has a File Meta Information header.
    /// </summary>
    public bool HasMetaHeader => _dicomFile?.FileMetaInfo != null &&
        _dicomFile.FileMetaInfo.Count() > 0;

    /// <summary>
    /// Parent object (for sequence items).
    /// </summary>
    public object? Parent { get; set; }

    /// <summary>
    /// Sequence name/keyword.
    /// </summary>
    public string SequenceName { get => _sequenceName; set => _sequenceName = value; }

    /// <summary>
    /// Sequence group number.
    /// </summary>
    public ushort SequenceGroup { get => _sequenceGroup; set => _sequenceGroup = value; }

    /// <summary>
    /// Sequence element number.
    /// </summary>
    public ushort SequenceElement { get => _sequenceElement; set => _sequenceElement = value; }

    /// <summary>
    /// Sequence description value.
    /// </summary>
    public string SequenceValue { get => _sequenceValue; set => _sequenceValue = value; }

    /// <summary>
    /// Parent item index (for nested items).
    /// </summary>
    public int ParentItemIndex { get => _parentItemIndex; set => _parentItemIndex = value; }

    /// <summary>
    /// Direct access to the underlying fo-dicom DicomDataset.
    /// </summary>
    public DicomDataset Dataset => _dataset;

    // ==============================================================================================
    // File I/O
    // ==============================================================================================

    /// <summary>
    /// Reads a DICOM header from a file.
    /// </summary>
    private void ReadDcmHeader(string fileName)
    {
        try
        {
            Clear();

            if (ReadPixelData)
                _dicomFile = DicomFile.Open(fileName);
            else
                _dicomFile = DicomFile.Open(fileName, FileReadOption.SkipLargeTags);

            _dataset = _dicomFile.Dataset;
            RefreshFlatTagList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ReadDcmHeader failed for {fileName}: {ex.Message}");
            Clear();
        }
    }

    /// <summary>
    /// Saves the dataset as a DICOM file.
    /// </summary>
    /// <param name="fileName">Output file path.</param>
    /// <returns>True if the file was saved successfully.</returns>
    public bool SaveAsDicom(string fileName)
    {
        try
        {
            var file = _dicomFile ?? new DicomFile(_dataset);

            // Ensure file meta information is present
            if (file.FileMetaInfo == null || file.FileMetaInfo.Count() == 0)
            {
                file = new DicomFile(_dataset);
            }

            file.Save(fileName);
            return File.Exists(fileName);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SaveAsDicom failed: {ex.Message}");
            return false;
        }
    }

    // ==============================================================================================
    // Tag Read Operations
    // ==============================================================================================

    /// <summary>
    /// Reads a tag value as a string. Handles text VR character set conversion automatically.
    /// </summary>
    /// <param name="tag">The DICOM tag to read.</param>
    /// <returns>The tag value as a trimmed string, or empty string if not found.</returns>
    public string ReadTagValue(DicomTag tag)
    {
        try
        {
            if (!_dataset.Contains(tag))
                return string.Empty;

            var item = _dataset.GetDicomItem<DicomItem>(tag);
            if (item is DicomSequence)
                return "SEQUENCE";

            return _dataset.GetString(tag)?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Reads a tag value by group and element numbers.
    /// </summary>
    public string ReadTagValue(ushort group, ushort element)
    {
        return ReadTagValue(new DicomTag(group, element));
    }

    /// <summary>
    /// Reads a tag and returns the full DicomTagValue object with metadata.
    /// </summary>
    /// <param name="tag">The DICOM tag to read.</param>
    /// <param name="tagValue">Output tag value object (null if tag is a sequence).</param>
    /// <param name="sequence">Output sequence object (null if tag is not a sequence).</param>
    /// <returns>True if the tag was found and is a value element.</returns>
    public bool ReadTag(DicomTag tag, out DicomTagValue? tagValue, out DicomHeaderObject? sequence)
    {
        tagValue = null;
        sequence = null;

        if (!_dataset.Contains(tag))
            return false;

        var item = _dataset.GetDicomItem<DicomItem>(tag);

        if (item is DicomSequence sq)
        {
            sequence = WrapSequence(sq);
            return false; // Delphi convention: returns false for sequences
        }

        tagValue = new DicomTagValue
        {
            Group = tag.Group,
            Element = tag.Element,
            VR = item.ValueRepresentation?.Code ?? string.Empty,
            Value = _dataset.GetString(tag) ?? string.Empty,
            Name = tag.DictionaryEntry?.Keyword ?? string.Empty,
        };

        return true;
    }

    /// <summary>
    /// Reads a tag value as raw bytes (for OB/OW VRs and pixel data).
    /// </summary>
    public byte[]? ReadTagValueAsBytes(DicomTag tag)
    {
        try
        {
            var item = _dataset.GetDicomItem<DicomItem>(tag);
            if (item is DicomElement element)
                return element.Buffer?.Data;
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Checks if a tag exists in the dataset.
    /// </summary>
    public bool TagExists(DicomTag tag)
    {
        return _dataset.Contains(tag);
    }

    /// <summary>
    /// Checks if a tag exists by group and element numbers.
    /// </summary>
    public bool TagExists(ushort group, ushort element)
    {
        return _dataset.Contains(new DicomTag(group, element));
    }

    // ==============================================================================================
    // Tag Write Operations
    // ==============================================================================================

    /// <summary>
    /// Adds or updates a tag with a string value. VR is determined from the DICOM dictionary.
    /// </summary>
    /// <param name="tag">The DICOM tag to set.</param>
    /// <param name="value">The string value to set.</param>
    /// <param name="vr">Optional explicit VR override.</param>
    /// <param name="name">Optional tag name (for display only).</param>
    /// <param name="vm">Optional VM string.</param>
    /// <returns>True if the tag was set successfully.</returns>
    public bool AddTag(DicomTag tag, string value, string? vr = null, string? name = null,
        string? vm = null)
    {
        try
        {
            var dicomVR = vr != null ? DicomVR.Parse(vr) : tag.DictionaryEntry.ValueRepresentations[0];

            _dataset.AddOrUpdate(dicomVR, new DicomTag(tag.Group, tag.Element), value);

            RefreshFlatTagList();
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AddTag failed for ({tag}): {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Adds or updates a tag by group and element with a string value.
    /// </summary>
    public bool AddTag(ushort group, ushort element, string value,
        string? vr = null, string? name = null, string? vm = null)
    {
        return AddTag(new DicomTag(group, element), value, vr, name, vm);
    }

    /// <summary>
    /// Adds a tag with Unicode text, using the specified character set for encoding.
    /// With fo-dicom, character set handling is automatic, so this delegates to AddTag.
    /// </summary>
    public bool AddTagUnicode(DicomTag tag, string value, string characterSet = "")
    {
        // fo-dicom handles character set encoding automatically via SpecificCharacterSet
        return AddTag(tag, value);
    }

    /// <summary>
    /// Adds or returns an existing sequence at the specified tag.
    /// </summary>
    /// <param name="tag">The sequence tag.</param>
    /// <returns>A DicomHeaderObject representing the sequence.</returns>
    public DicomHeaderObject AddSequence(DicomTag tag)
    {
        DicomSequence sq;
        if (_dataset.Contains(tag))
        {
            sq = _dataset.GetSequence(tag);
        }
        else
        {
            sq = new DicomSequence(tag);
            _dataset.AddOrUpdate(sq);
        }

        var seqObj = WrapSequence(sq);
        RefreshFlatTagList();
        return seqObj;
    }

    /// <summary>
    /// Adds a new item to this sequence and returns it.
    /// </summary>
    public DicomHeaderObject? AddItem()
    {
        if (!_isSequence)
            return null;

        var itemDataset = new DicomDataset();
        var item = new DicomHeaderObject(itemDataset)
        {
            IsSequenceItem = true,
            Parent = this,
            ParentItemIndex = _items.Count,
        };
        _items.Add(item);
        _itemIndex = _items.Count - 1;
        return item;
    }

    /// <summary>
    /// Deletes a tag from the dataset.
    /// </summary>
    public bool DeleteTag(DicomTag tag)
    {
        if (!_dataset.Contains(tag))
            return false;

        _dataset.Remove(tag);
        RefreshFlatTagList();
        return true;
    }

    /// <summary>
    /// Deletes a tag by group and element numbers.
    /// </summary>
    public bool DeleteTag(ushort group, ushort element)
    {
        return DeleteTag(new DicomTag(group, element));
    }

    /// <summary>
    /// Deletes a sequence item by 1-based index.
    /// </summary>
    public bool DeleteItem(int index)
    {
        if (index < 1 || index > _items.Count)
            return false;

        _items[index - 1].Dispose();
        _items.RemoveAt(index - 1);
        return true;
    }

    /// <summary>
    /// Clears all tags and items.
    /// </summary>
    public void Clear()
    {
        _dataset = new DicomDataset();
        _dicomFile = null;

        foreach (var item in _items)
            item.Dispose();
        _items.Clear();

        _flatTagList.Clear();
        _currentTagIndex = -1;
        _itemIndex = -1;
    }

    // ==============================================================================================
    // Tag Navigation (mirrors Delphi's GotoFirstTag/GotoNextTag pattern)
    // ==============================================================================================

    /// <summary>
    /// Refreshes the flat tag list used for sequential navigation.
    /// </summary>
    private void RefreshFlatTagList()
    {
        _flatTagList.Clear();
        _flatTagList.AddRange(_dataset);
        _currentTagIndex = -1;
    }

    /// <summary>
    /// Moves to the first tag in the dataset.
    /// </summary>
    /// <returns>True if there is at least one tag.</returns>
    public bool GotoFirstTag()
    {
        RefreshFlatTagList();
        if (_flatTagList.Count == 0)
            return false;
        _currentTagIndex = 0;
        return true;
    }

    /// <summary>
    /// Moves to the first tag and returns its group and element.
    /// </summary>
    public bool GotoFirstTag(out ushort group, out ushort element)
    {
        group = 0;
        element = 0;
        if (!GotoFirstTag())
            return false;
        group = _flatTagList[_currentTagIndex].Tag.Group;
        element = _flatTagList[_currentTagIndex].Tag.Element;
        return true;
    }

    /// <summary>
    /// Moves to the next tag in the dataset.
    /// </summary>
    /// <returns>True if there is a next tag.</returns>
    public bool GotoNextTag()
    {
        if (_currentTagIndex < 0 || _currentTagIndex >= _flatTagList.Count - 1)
            return false;
        _currentTagIndex++;
        return true;
    }

    /// <summary>
    /// Moves to the next tag and returns its group and element.
    /// </summary>
    public bool GotoNextTag(out ushort group, out ushort element)
    {
        group = 0;
        element = 0;
        if (!GotoNextTag())
            return false;
        group = _flatTagList[_currentTagIndex].Tag.Group;
        element = _flatTagList[_currentTagIndex].Tag.Element;
        return true;
    }

    /// <summary>
    /// Gets the current tag at the navigation position.
    /// </summary>
    public DicomItem? GetCurrentItem()
    {
        if (_currentTagIndex >= 0 && _currentTagIndex < _flatTagList.Count)
            return _flatTagList[_currentTagIndex];
        return null;
    }

    /// <summary>
    /// Deletes the tag at the current navigation position.
    /// </summary>
    public bool DeleteCurrentTag()
    {
        var item = GetCurrentItem();
        if (item == null)
            return false;

        _dataset.Remove(item.Tag);
        _flatTagList.RemoveAt(_currentTagIndex);
        if (_currentTagIndex >= _flatTagList.Count)
            _currentTagIndex = _flatTagList.Count - 1;
        return true;
    }

    // ==============================================================================================
    // Sequence Item Navigation
    // ==============================================================================================

    /// <summary>
    /// Moves to the first item in this sequence.
    /// </summary>
    public DicomHeaderObject? GotoFirstItem()
    {
        _itemIndex = 0;
        if (!HasItems)
            return null;
        return _items[0];
    }

    /// <summary>
    /// Moves to the next item in this sequence.
    /// </summary>
    public DicomHeaderObject? GotoNextItem()
    {
        if (!HasItems || _itemIndex + 1 >= _items.Count)
            return null;
        _itemIndex++;
        return _items[_itemIndex];
    }

    /// <summary>
    /// Moves to the last item in this sequence.
    /// </summary>
    public DicomHeaderObject? GotoLastItem()
    {
        if (!HasItems)
            return null;
        _itemIndex = _items.Count - 1;
        return _items[_itemIndex];
    }

    /// <summary>
    /// Gets the current item index.
    /// </summary>
    public int ItemIndex => _itemIndex;

    /// <summary>
    /// Gets an item by 0-based index.
    /// </summary>
    public DicomHeaderObject? GetItem(int index)
    {
        if (index >= 0 && index < _items.Count)
            return _items[index];
        return null;
    }

    // ==============================================================================================
    // Study-Level Tag Assignment
    // ==============================================================================================

    /// <summary>
    /// Copies study-level tags from another dataset into this one.
    /// </summary>
    public void AssignStudyLevelTags(DicomHeaderObject source)
    {
        var charSet = source.ReadTagValue(DicomTagConstants.SpecificCharacterSet);

        AddTag(DicomTagConstants.SpecificCharacterSet, charSet);
        AddTag(DicomTagConstants.StudyInstanceUID, source.ReadTagValue(DicomTagConstants.StudyInstanceUID));
        AddTag(DicomTagConstants.StudyID, source.ReadTagValue(DicomTagConstants.StudyID));
        AddTagUnicode(DicomTagConstants.StudyDescription, source.ReadTagValue(DicomTagConstants.StudyDescription), charSet);
        AddTagUnicode(DicomTagConstants.PatientName, source.ReadTagValue(DicomTagConstants.PatientName), charSet);
        AddTagUnicode(DicomTagConstants.PatientID, source.ReadTagValue(DicomTagConstants.PatientID), charSet);
        AddTag(DicomTagConstants.PatientBirthDate, source.ReadTagValue(DicomTagConstants.PatientBirthDate));
        AddTag(DicomTag.PatientSex, source.ReadTagValue(DicomTag.PatientSex));
        AddTag(DicomTagConstants.AccessionNumber, source.ReadTagValue(DicomTagConstants.AccessionNumber));
        AddTag(DicomTagConstants.StudyDate, source.ReadTagValue(DicomTagConstants.StudyDate));
        AddTag(DicomTagConstants.StudyTime, source.ReadTagValue(DicomTagConstants.StudyTime));
        AddTagUnicode(DicomTagConstants.ReferringPhysicianName,
            source.ReadTagValue(DicomTagConstants.ReferringPhysicianName), charSet);

        var rs = source.ReadTagValue(DicomTag.RescaleSlope);
        var ri = source.ReadTagValue(DicomTag.RescaleIntercept);
        if (!string.IsNullOrEmpty(rs) && !string.IsNullOrEmpty(ri))
        {
            AddTag(DicomTag.RescaleSlope, rs);
            AddTag(DicomTag.RescaleIntercept, ri);
        }

        var rt = source.ReadTagValue(DicomTag.RescaleType);
        if (string.IsNullOrEmpty(rt))
            rt = "US";
        AddTag(DicomTag.RescaleType, rt);
    }

    // ==============================================================================================
    // Tag List / Display
    // ==============================================================================================

    /// <summary>
    /// Fills a list with human-readable tag dump lines.
    /// Equivalent to TdcmHdrObj.FillStringList.
    /// </summary>
    public void FillStringList(List<string> list, string prefix = "")
    {
        if (_isSequence)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                list.Add($"{prefix}>>[ITEM {i + 1}] =");
                _items[i].FillStringList(list, prefix + ">>");
            }
            return;
        }

        foreach (var item in _dataset)
        {
            var tag = item.Tag;
            var groupStr = $"{tag.Group:X4}";
            var elementStr = $"{tag.Element:X4}";
            var keyword = tag.DictionaryEntry?.Keyword ?? "Unknown";

            if (item is DicomSequence sq)
            {
                list.Add($"{prefix}{groupStr},{elementStr} [{keyword}]");
                var seqObj = WrapSequence(sq);
                seqObj.FillStringList(list, prefix);
            }
            else
            {
                var value = _dataset.GetString(tag) ?? string.Empty;
                list.Add($"{prefix}{groupStr},{elementStr} [{keyword}] = {value}");
            }
        }
    }

    /// <summary>
    /// Gets the number of elements in a specific DICOM group.
    /// </summary>
    public int ElementsInGroup(ushort group)
    {
        return _dataset.Count(i => i.Tag.Group == group);
    }

    // ==============================================================================================
    // Enhanced DICOM Multi-frame Helpers
    // ==============================================================================================

    /// <summary>
    /// Gets the Image Orientation Patient for a specific frame in enhanced DICOM objects.
    /// </summary>
    public string GetEnhancedIOP(int frameIndex)
    {
        // TODO: Implement enhanced multi-frame per-frame functional group access
        return ReadTagValue(DicomTagConstants.ImageOrientationPatient);
    }

    /// <summary>
    /// Gets the Image Position Patient for a specific frame in enhanced DICOM objects.
    /// </summary>
    public string GetEnhancedIPP(int frameIndex)
    {
        // TODO: Implement enhanced multi-frame per-frame functional group access
        return ReadTagValue(DicomTagConstants.ImagePositionPatient);
    }

    /// <summary>
    /// Gets pixel spacing for a specific frame in enhanced DICOM objects.
    /// </summary>
    public string GetEnhancedPixelSpacing(int frameIndex)
    {
        // TODO: Implement enhanced multi-frame per-frame functional group access
        return ReadTagValue(DicomTagConstants.PixelSpacing);
    }

    /// <summary>
    /// Gets rescale slope/intercept for a specific frame in enhanced DICOM objects.
    /// </summary>
    public (double slope, double intercept) GetEnhancedSlopeIntercept(int frameIndex)
    {
        // TODO: Implement enhanced multi-frame per-frame functional group access
        var slope = 1.0;
        var intercept = 0.0;
        if (double.TryParse(ReadTagValue(DicomTag.RescaleSlope), out var s)) slope = s;
        if (double.TryParse(ReadTagValue(DicomTag.RescaleIntercept), out var i)) intercept = i;
        return (slope, intercept);
    }

    /// <summary>
    /// Gets window center/width for a specific frame in enhanced DICOM objects.
    /// </summary>
    public (double center, double width) GetEnhancedWindowCenterWidth(int frameIndex)
    {
        // TODO: Implement enhanced multi-frame per-frame functional group access
        var center = 0.0;
        var width = 0.0;
        if (double.TryParse(ReadTagValue(DicomTag.WindowCenter), out var c)) center = c;
        if (double.TryParse(ReadTagValue(DicomTag.WindowWidth), out var w)) width = w;
        return (center, width);
    }

    // ==============================================================================================
    // Pixel Data Access
    // ==============================================================================================

    /// <summary>
    /// Gets the pixel data buffer for a specific frame.
    /// </summary>
    /// <param name="frameIndex">0-based frame index.</param>
    /// <returns>Byte array containing pixel data, or null if not available.</returns>
    public byte[]? GetPixelDataOfFrame(int frameIndex = 0)
    {
        try
        {
            if (!_dataset.Contains(DicomTag.PixelData))
                return null;

            var pixelData = DicomPixelData.Create(_dataset);
            if (frameIndex >= pixelData.NumberOfFrames)
                return null;

            var frame = pixelData.GetFrame(frameIndex);
            return frame.Data;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Calculates the distance between consecutive slices using Image Position Patient.
    /// </summary>
    public double CalcDistanceBetweenSlices(DicomHeaderObject otherSlice)
    {
        var ipp1 = ReadTagValue(DicomTagConstants.ImagePositionPatient);
        var ipp2 = otherSlice.ReadTagValue(DicomTagConstants.ImagePositionPatient);

        if (string.IsNullOrEmpty(ipp1) || string.IsNullOrEmpty(ipp2))
            return 0;

        var parts1 = ipp1.Split('\\');
        var parts2 = ipp2.Split('\\');

        if (parts1.Length < 3 || parts2.Length < 3)
            return 0;

        try
        {
            var x1 = double.Parse(parts1[0]); var y1 = double.Parse(parts1[1]); var z1 = double.Parse(parts1[2]);
            var x2 = double.Parse(parts2[0]); var y2 = double.Parse(parts2[1]); var z2 = double.Parse(parts2[2]);

            return Math.Sqrt(Math.Pow(x2 - x1, 2) + Math.Pow(y2 - y1, 2) + Math.Pow(z2 - z1, 2));
        }
        catch
        {
            return 0;
        }
    }

    // ==============================================================================================
    // Helpers
    // ==============================================================================================

    /// <summary>
    /// Wraps a fo-dicom DicomSequence into a DicomHeaderObject for navigation.
    /// </summary>
    private DicomHeaderObject WrapSequence(DicomSequence sq)
    {
        var seqObj = new DicomHeaderObject
        {
            IsSequence = true,
            SequenceName = sq.Tag.DictionaryEntry?.Keyword ?? "Sequence",
            SequenceGroup = sq.Tag.Group,
            SequenceElement = sq.Tag.Element,
            Parent = this,
        };

        foreach (var itemDataset in sq.Items)
        {
            var itemObj = new DicomHeaderObject(itemDataset)
            {
                IsSequenceItem = true,
                Parent = seqObj,
                ParentItemIndex = seqObj._items.Count,
            };
            seqObj._items.Add(itemObj);
        }

        return seqObj;
    }

    /// <summary>
    /// Enumerates all tags in the dataset, yielding (DicomTag, string value) pairs.
    /// </summary>
    public IEnumerable<(DicomTag Tag, string Value)> EnumerateTags()
    {
        foreach (var item in _dataset)
        {
            if (item is DicomSequence)
                yield return (item.Tag, "SEQUENCE");
            else
            {
                var value = _dataset.GetString(item.Tag) ?? string.Empty;
                yield return (item.Tag, value);
            }
        }
    }

    // ==============================================================================================
    // IDisposable
    // ==============================================================================================

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                foreach (var item in _items)
                    item.Dispose();
                _items.Clear();
            }
            _disposed = true;
        }
    }
}
