package com.goping.web.dto;

import com.fasterxml.jackson.annotation.JsonInclude;

public final class ErrorDtos {

    private ErrorDtos() {
    }

    public record ErrorEnvelope(ErrorBody error) {
    }

    public record ErrorBody(
            String code,
            String message,
            @JsonInclude(JsonInclude.Include.NON_NULL) Object details,
            String requestId
    ) {
    }
}
