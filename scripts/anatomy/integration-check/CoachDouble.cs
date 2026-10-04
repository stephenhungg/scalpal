using UnityEngine;
using Scalpal.Exercises.Engine;
namespace Scalpal.Exercises.Coach
{
    // Transport-independent fixture. Actual CoachRelay lifecycle has a separate relay-check suite.
    public class CoachRelay : MonoBehaviour
    {
        public string Endpoint = "";
        public void ConfigureEndpoint(string url) { Endpoint = url; }
        public bool Connected, IsSynchronized;
        public string SessionId = "session-1";
        public string SessionPatientId = "", SessionProcedureId = "", SessionInitialStepId = "";
        public string SessionCaseId = "", SessionMode = "";
        public int forwarded;
        public void UseSession(string sessionId) { }
        public void AdoptCurrentSession(string patient, string procedure, string caseId, string mode, string firstStep) { }
        public void AdoptSession(string sessionId, string patient, string procedure, string caseId, string mode, string firstStep) { }
        public void Forward(CaseEvent input, string outcome = "") { forwarded++; }
    }
}
