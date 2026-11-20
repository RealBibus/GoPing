package com.goping.web;

import com.goping.domain.Chat;
import com.goping.repository.ChatRepository;
import com.goping.security.RequestContext;
import com.goping.web.dto.ApiDtos;
import jakarta.servlet.http.HttpServletRequest;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.RestController;

import java.util.List;

@RestController
@RequestMapping("/api/v1/chats")
public class ChatController {

    private final ChatRepository chats;
    private final DtoMapper mapper;

    public ChatController(ChatRepository chats, DtoMapper mapper) {
        this.chats = chats;
        this.mapper = mapper;
    }

    /**
     * Пока чатов нет — items пустой, клиент показывает заглушку.
     * Формат ответа финальный: когда появятся чаты, контракт не изменится.
     */
    @GetMapping
    public ApiDtos.ChatListResponse list(
            @RequestParam(defaultValue = "30") int limit,
            HttpServletRequest request) {
        int safeLimit = Math.max(1, Math.min(limit, 100));
        List<Chat> found = chats.listForUser(RequestContext.userId(request), safeLimit);
        List<ApiDtos.ChatDto> items = found.stream().map(mapper::chat).toList();
        return new ApiDtos.ChatListResponse(items, null);
    }
}
