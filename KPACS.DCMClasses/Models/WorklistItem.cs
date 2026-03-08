// ------------------------------------------------------------------------------------------------
// KPACS.DCMClasses - Models/WorklistItem.cs
// Ported from DCMNetClass.pas (TWorklistItem record)
// ------------------------------------------------------------------------------------------------

namespace KPACS.DCMClasses.Models;

/// <summary>
/// DICOM Modality Worklist item.
/// </summary>
public class WorklistItem
{
    public string PatName { get; set; } = string.Empty;
    public string PatId { get; set; } = string.Empty;
    public string AccNo { get; set; } = string.Empty;
    public string ReqPhysician { get; set; } = string.Empty;
    public string PatBD { get; set; } = string.Empty;
    public string PatSex { get; set; } = string.Empty;
    public string MedicalAlerts { get; set; } = string.Empty;
    public string ContrastAllergies { get; set; } = string.Empty;
    public string StudyInstanceUid { get; set; } = string.Empty;
    public string RequestedProcedureDescription { get; set; } = string.Empty;
    public string SppDescription { get; set; } = string.Empty;
    public string SppStartTime { get; set; } = string.Empty;
    public string SppStartDate { get; set; } = string.Empty;
    public string SppModality { get; set; } = string.Empty;
    public string SppAeTitle { get; set; } = string.Empty;
    public string SppPhysName { get; set; } = string.Empty;
    public string SppReqContrAgent { get; set; } = string.Empty;
    public string SppProcId { get; set; } = string.Empty;
    public string SppStationName { get; set; } = string.Empty;
    public string SppLocation { get; set; } = string.Empty;
    public string SppPreMed { get; set; } = string.Empty;
    public string ReqProcId { get; set; } = string.Empty;
    public string ReqPriority { get; set; } = string.Empty;
}
