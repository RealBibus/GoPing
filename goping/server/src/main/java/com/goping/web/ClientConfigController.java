package com.goping.web;

import com.goping.config.DeletePolicy;
import com.goping.web.dto.ApiDtos;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RestController;

/** Настройки, которые клиенту нужно знать до открытия чата. */
@RestController
@RequestMapping("/api/v1/client-config")
public class ClientConfigController {

    private final DeletePolicy deletePolicy;

    public ClientConfigController(DeletePolicy deletePolicy) {
        this.deletePolicy = deletePolicy;
    }

    @GetMapping
    public ApiDtos.ClientConfigDto get() {
        return new ApiDtos.ClientConfigDto(deletePolicy.isAllowDeleteOthers());
    }
}
