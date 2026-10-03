using System;
using Scalpal.Exercises.Data;
using UnityEngine;
namespace Scalpal.Exercises.Preop
{
    // Event-only double. Does not exercise HTTP or the offline fallback client.
    public class ScalpalPreopService : MonoBehaviour
    {
        public event Action<SurgicalCase> CaseLoaded;
        public event Action<ErrorResponse> RequestFailed;
        public string RequestedPatient;
        public string BaseUrl = "http://localhost:8787";
        public void LoadCase(string patientId) { RequestedPatient = patientId; }
        public void Deliver(SurgicalCase item) { CaseLoaded?.Invoke(item); }
        public void Fail(string message) { RequestFailed?.Invoke(new ErrorResponse { error = new ErrorInfo { message = message } }); }
    }
}
