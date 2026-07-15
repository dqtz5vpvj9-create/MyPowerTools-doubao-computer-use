namespace DoubaoAgent.Surface.Services;

// Operation-result record returned by DoubaoSecureRuntimeController. The canonical definition
// lives in DoubaoAgentToolService.cs (Surface); this shim mirrors its shape in the same namespace
// so the linked DoubaoSecureRuntimeController.cs compiles in the worker without pulling in the
// Avalonia/SDK-dependent Surface project. Kept structurally identical to the Surface type.
public sealed record DoubaoAgentOperationResult(bool Success, string Message, string TechnicalDetails = "");
