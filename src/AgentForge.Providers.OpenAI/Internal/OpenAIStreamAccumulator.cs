using System.Text;
using AgentForge.Abstractions;
using OpenAI.Responses;

namespace AgentForge.Providers.OpenAI.Internal;

internal sealed class OpenAIStreamAccumulator
{
    private readonly Dictionary<int, FragmentState> _fragments = [];
    private readonly StringBuilder _text = new();
    private ResponseResult? _terminal;

    public bool HasTerminal => _terminal is not null;

    public ModelTextDelta? Accept(StreamingResponseUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (_terminal is not null)
            throw OpenAIExceptionMapper.Protocol("The provider stream emitted an event after its terminal event.");

        switch (update)
        {
            case StreamingResponseOutputTextDeltaUpdate text:
                if (string.IsNullOrEmpty(text.Delta))
                    throw OpenAIExceptionMapper.Protocol("The provider stream emitted an invalid text delta.");
                _text.Append(text.Delta);
                return new ModelTextDelta(text.Delta);
            case StreamingResponseFunctionCallArgumentsDeltaUpdate delta:
                State(delta.OutputIndex, delta.ItemId).Append(delta.Delta);
                break;
            case StreamingResponseFunctionCallArgumentsDoneUpdate done:
                State(done.OutputIndex, done.ItemId).Complete(done.FunctionName, done.FunctionArguments);
                break;
            case StreamingResponseCompletedUpdate completed:
                _terminal = completed.Response ?? throw OpenAIExceptionMapper.Protocol("The provider terminal event has no response.");
                if (_terminal.Status != ResponseStatus.Completed)
                    throw OpenAIExceptionMapper.Protocol("The provider completion event contains a contradictory status.");
                break;
            case StreamingResponseIncompleteUpdate incomplete:
                _terminal = incomplete.Response ?? throw OpenAIExceptionMapper.Protocol("The provider terminal event has no response.");
                if (_terminal.Status != ResponseStatus.Incomplete && _terminal.Status != ResponseStatus.Cancelled)
                    throw OpenAIExceptionMapper.Protocol("The provider incomplete event contains a contradictory status.");
                break;
            case StreamingResponseFailedUpdate failed:
                throw OpenAIExceptionMapper.FromResponseError(failed.Response?.Error);
            case StreamingResponseErrorUpdate error:
                throw ClassifyErrorCode(error.Code);
        }
        return null;
    }

    public ModelResponse Complete()
    {
        if (_terminal is null)
            throw OpenAIExceptionMapper.Protocol("The provider stream ended before a terminal event.");

        foreach (var pair in _fragments)
        {
            if (pair.Key < 0 || pair.Key >= _terminal.OutputItems.Count
                || _terminal.OutputItems[pair.Key] is not FunctionCallResponseItem call)
                throw OpenAIExceptionMapper.Protocol("The provider stream contains an uncorrelated function call.");
            pair.Value.Validate(call);
        }
        var response = OpenAIResponseMapper.Map(_terminal);
        if (_text.Length > 0 && !StringComparer.Ordinal.Equals(_text.ToString(), response.Message.Text))
            throw OpenAIExceptionMapper.Protocol("The provider stream text contradicts the terminal response.");
        return response;
    }

    private FragmentState State(int index, string? itemId)
    {
        if (index < 0 || string.IsNullOrWhiteSpace(itemId))
            throw OpenAIExceptionMapper.Protocol("The provider stream contains an invalid function-call identity.");
        if (!_fragments.TryGetValue(index, out var state))
            _fragments.Add(index, state = new FragmentState(itemId));
        else if (!StringComparer.Ordinal.Equals(state.ItemId, itemId))
            throw OpenAIExceptionMapper.Protocol("The provider stream contains conflicting function-call identities.");
        return state;
    }

    private static ModelProviderException ClassifyErrorCode(string? code) => code switch
    {
        "rate_limit_exceeded" => new(ModelProviderErrorKind.RateLimited, true, "The OpenAI provider request failed."),
        "server_error" => new(ModelProviderErrorKind.ServiceUnavailable, true, "The OpenAI provider request failed."),
        "invalid_prompt" => new(ModelProviderErrorKind.InvalidRequest, false, "The OpenAI provider request failed."),
        _ => new(ModelProviderErrorKind.Unknown, false, "The OpenAI provider request failed.")
    };

    private sealed class FragmentState(string itemId)
    {
        private readonly StringBuilder _arguments = new();
        private string? _doneArguments;
        private string? _functionName;
        public string ItemId { get; } = itemId;

        public void Append(BinaryData? delta)
        {
            if (_doneArguments is not null || delta is null)
                throw OpenAIExceptionMapper.Protocol("The provider stream contains invalid function arguments.");
            _arguments.Append(delta.ToString());
        }

        public void Complete(string? functionName, BinaryData? arguments)
        {
            if (_doneArguments is not null || string.IsNullOrWhiteSpace(functionName) || arguments is null)
                throw OpenAIExceptionMapper.Protocol("The provider stream contains invalid completed function arguments.");
            _functionName = functionName;
            _doneArguments = arguments.ToString();
            if (_arguments.Length > 0 && !StringComparer.Ordinal.Equals(_arguments.ToString(), _doneArguments))
                throw OpenAIExceptionMapper.Protocol("The provider stream contains conflicting function arguments.");
        }

        public void Validate(FunctionCallResponseItem call)
        {
            if (!string.IsNullOrEmpty(call.Id) && !StringComparer.Ordinal.Equals(ItemId, call.Id))
                throw OpenAIExceptionMapper.Protocol("The provider stream function identity contradicts the terminal response.");
            var terminalArguments = call.FunctionArguments?.ToString();
            if (_arguments.Length > 0 && !StringComparer.Ordinal.Equals(_arguments.ToString(), terminalArguments))
                throw OpenAIExceptionMapper.Protocol("The provider stream arguments contradict the terminal response.");
            if (_doneArguments is not null && (!StringComparer.Ordinal.Equals(_doneArguments, terminalArguments)
                || !StringComparer.Ordinal.Equals(_functionName, call.FunctionName)))
                throw OpenAIExceptionMapper.Protocol("The provider stream function call contradicts the terminal response.");
        }
    }
}
