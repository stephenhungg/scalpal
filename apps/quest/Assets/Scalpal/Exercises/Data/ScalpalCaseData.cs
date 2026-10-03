// JsonUtility DTOs for the scalpal-preop service (services/preop). Field names must match the JSON
// keys exactly: services/preop/test/unity-contract.test.ts fails if a payload key has no field here or
// a field here never appears in a payload. Pure C# (no UnityEngine) so it also compiles in the .NET check.
using System;

namespace Scalpal.Exercises.Data
{
    [Serializable]
    public class ScalpalAction
    {
        public string id;
        public string label;
        public string method;
        public string route;
    }

    [Serializable]
    public class ErrorInfo
    {
        public string code;
        public string message;
    }

    [Serializable]
    public class ErrorResponse
    {
        public ErrorInfo error;
        public ScalpalAction[] actions;
    }

    [Serializable]
    public class Evidence
    {
        public string kind;
        public string label;
        public string value;
        public string date;
        public string source;
    }

    [Serializable]
    public class RiskFlag
    {
        public string id;
        public string type;
        public string severity;
        public string title;
        public string spoken;
        public string detail;
        public string[] structures;
        public Evidence[] evidence;
    }

    [Serializable]
    public class DataGap
    {
        public string code;
        public string message;
    }

    [Serializable]
    public class PatientSummary
    {
        public string name;
        public int age;
        public string sex;
        public string displayLabel;
    }

    [Serializable]
    public class ChartLine
    {
        public string section;
        public string text;
        public bool flagged;
    }

    [Serializable]
    public class PreopBrief
    {
        public string patientId;
        public bool synthetic;
        public string generatedAt;
        public string dataAsOf;
        public PatientSummary patient;
        public RiskFlag[] flags;
        public string[] highlightStructures;
        public int activeMedicationCount;
        public ChartLine[] chart;
        public DataGap[] dataGaps;
        public string[] sources;
        public string say;
        public string disclaimer;
        public ScalpalAction[] actions;
    }

    // Torso frame, meters: +x patient's left, +y anterior, +z cranial, origin at the umbilicus.
    [Serializable]
    public struct Vec3
    {
        public float x;
        public float y;
        public float z;
    }

    [Serializable]
    public class AnatomyStructure
    {
        public string id;
        public string unityName;
        public string displayName;
        public string system;
        public string region;
    }

    [Serializable]
    public class Instrument
    {
        public string id;
        public string unityPrefab;
        public string displayName;
        public string kind;
    }

    [Serializable]
    public class Port
    {
        public string id;
        public string label;
        public int sizeMm;
        public Vec3 position;
        public string[] instrumentIds;
    }

    [Serializable]
    public class SuccessCheck
    {
        public string type;
        public string[] targets;
        public int count;
    }

    [Serializable]
    public class StepMistake
    {
        public string id;
        public string trigger;
        public string structure;
        public string severity;
        public string feedback;
    }

    [Serializable]
    public class ProcedureStep
    {
        public string id;
        public string title;
        public string instruction;
        public string action;
        public string instrumentId;
        public string[] targets;
        public string[] portIds;
        public SuccessCheck check;
        public StepMistake[] mistakes;
        public string[] hints;
        public string next;
    }

    [Serializable]
    public class Procedure
    {
        public string id;
        public string title;
        public string shortTitle;
        public string approach;
        public string summary;
        public int typicalMinutes;
        public string[] structures;
        public string[] focusStructures;
        public Port[] ports;
        public ProcedureStep[] steps;
        public string firstStep;
        public ScalpalAction[] actions;
    }

    [Serializable]
    public class CaseConsideration
    {
        public string flagId;
        public string stepId;
        public string note;
    }

    [Serializable]
    public class ChecklistOption
    {
        public string type;
        public string label;
    }

    [Serializable]
    public class SurgicalCase
    {
        public string caseId;
        public string patientId;
        public string scenarioId;
        public string status;
        public string statusReason;
        public int retryAfterSeconds;
        public PatientSummary patient;
        public float bodyScale;
        public string urgency;
        public string indication;
        public string presentation;
        public string procedureId;
        public Procedure procedure;
        public PreopBrief brief;
        public CaseConsideration[] considerations;
        public ChecklistOption[] checklistOptions;
        public Instrument[] instruments;
        public AnatomyStructure[] anatomy;
        public ScalpalAction[] actions;
        public string disclaimer;
    }

    [Serializable]
    public class PatientListEntry
    {
        public string patientId;
        public string scenarioId;
        public string kind;
        public string title;
        public string displayLabel;
        public string procedureId;
        public string procedureTitle;
        public string urgency;
        public string status;
        public int flagCount;
        public ScalpalAction[] actions;
    }

    [Serializable]
    public class PatientList
    {
        public PatientListEntry[] patients;
        public ScalpalAction[] actions;
    }

    [Serializable]
    public class PreopCheckRequest
    {
        public string[] selected;
    }

    [Serializable]
    public class PreopCheckResult
    {
        public string patientId;
        public string caseId;
        public ChecklistOption[] caught;
        public ChecklistOption[] missed;
        public ChecklistOption[] falseAlarms;
        public int score;
        public int total;
        public bool passed;
        public string[] feedback;
        public string say;
        public ScalpalAction[] actions;
    }

    [Serializable]
    public class ConnectResult
    {
        public string sessionId;
        public string scenarioId;
        public string status;
        public string failureCode;
        public string failureMessage;
        public string patientId;
        public string say;
        public ScalpalAction[] actions;
    }

    [Serializable]
    public class ProcedureSummary
    {
        public string id;
        public string title;
        public string shortTitle;
        public string summary;
        public int stepCount;
        public ScalpalAction[] actions;
    }

    [Serializable]
    public class ProcedureList
    {
        public ProcedureSummary[] procedures;
        public ScalpalAction[] actions;
    }

    [Serializable]
    public class AnatomyList
    {
        public AnatomyStructure[] structures;
        public ScalpalAction[] actions;
    }

    [Serializable]
    public class InstrumentList
    {
        public Instrument[] instruments;
        public ScalpalAction[] actions;
    }

    [Serializable]
    public class ServiceIndex
    {
        public string service;
        public string description;
        public string disclaimer;
        public ScalpalAction[] actions;
    }

    [Serializable]
    public class HealthStatus
    {
        public bool ok;
        public ScalpalAction[] actions;
    }

    // Offline bundle: everything needed to run every case without a network (Resources/scalpal_bundle.json).
    [Serializable]
    public class ScalpalBundle
    {
        public string generatedAt;
        public AnatomyStructure[] anatomy;
        public Instrument[] instruments;
        public Procedure[] procedures;
        public PatientListEntry[] patients;
        public SurgicalCase[] cases;
        public string disclaimer;
        public ScalpalAction[] actions;
    }
}
