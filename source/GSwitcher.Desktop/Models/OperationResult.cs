using System.Runtime.Serialization;

namespace GSwitcher.Models
{
    [DataContract]
    public sealed class OperationResult
    {
        [DataMember]
        public bool Success { get; set; }

        [DataMember]
        public string Message { get; set; }

        [DataMember]
        public string ErrorDetail { get; set; }

        [DataMember]
        public string ControlPath { get; set; }

        [DataMember]
        public string RequestedMode { get; set; }

        [DataMember]
        public string VerifiedMode { get; set; }

        [DataMember]
        public bool Verified { get; set; }

        [DataMember]
        public bool RequiresRestart { get; set; }

        [DataMember]
        public string Warning { get; set; }

        public static OperationResult Ok(
            string message,
            string controlPath = null,
            string requestedMode = null,
            string verifiedMode = null,
            bool verified = true,
            bool requiresRestart = false,
            string warning = null)
        {
            return new OperationResult
            {
                Success = true,
                Message = message,
                ControlPath = controlPath,
                RequestedMode = requestedMode,
                VerifiedMode = verifiedMode,
                Verified = verified,
                RequiresRestart = requiresRestart,
                Warning = warning
            };
        }

        public static OperationResult Fail(
            string message,
            string errorDetail = null,
            string controlPath = null,
            string requestedMode = null,
            string verifiedMode = null,
            bool verified = false,
            bool requiresRestart = false,
            string warning = null)
        {
            return new OperationResult
            {
                Success = false,
                Message = message,
                ErrorDetail = errorDetail,
                ControlPath = controlPath,
                RequestedMode = requestedMode,
                VerifiedMode = verifiedMode,
                Verified = verified,
                RequiresRestart = requiresRestart,
                Warning = warning
            };
        }
    }
}
