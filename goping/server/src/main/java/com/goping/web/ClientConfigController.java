package com.goping.web;

import com.goping.config.DeletePolicy;
import com.goping.config.LiveLimits;
import com.goping.web.dto.ApiDtos;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RestController;

/** Settings the client needs before it opens a chat. */
@RestController
@RequestMapping("/api/v1/client-config")
public class ClientConfigController {

    private final DeletePolicy deletePolicy;
    private final LiveLimits limits;

    public ClientConfigController(DeletePolicy deletePolicy, LiveLimits limits) {
        this.deletePolicy = deletePolicy;
        this.limits = limits;
    }

    @GetMapping
    public ApiDtos.ClientConfigDto get() {
        return new ApiDtos.ClientConfigDto(deletePolicy.isAllowDeleteOthers(), dto(limits.get()));
    }

    public static ApiDtos.LimitsDto dto(LiveLimits.Limits limits) {
        return new ApiDtos.LimitsDto(
                limits.maxTextLength(),
                limits.maxVoiceSeconds(),
                limits.maxCallMinutes(),
                limits.maxFileBytes());
    }
}
