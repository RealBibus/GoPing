package com.goping.web;

import com.goping.error.ApiException;
import com.goping.realtime.RealtimeEvents;
import com.goping.security.RequestContext;
import com.goping.service.PollService;
import com.goping.web.dto.ApiDtos;
import jakarta.servlet.http.HttpServletRequest;
import org.springframework.http.HttpStatus;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RestController;

import java.util.UUID;

/** Poll REST endpoints for chat members. */
@RestController
@RequestMapping("/api/v1/chats/{chatId}/polls")
public class PollController {

    private final PollService polls;
    private final RealtimeEvents realtime;
    private final DtoMapper mapper;

    public PollController(PollService polls, RealtimeEvents realtime, DtoMapper mapper) {
        this.polls = polls;
        this.realtime = realtime;
        this.mapper = mapper;
    }

    @PostMapping
    public ResponseEntity<ApiDtos.PollDto> create(
            @PathVariable String chatId,
            @RequestBody(required = false) ApiDtos.CreatePollRequest body,
            HttpServletRequest request) {
        UUID chat = parseChatId(chatId);
        String question = body == null ? null : body.question();
        PollService.CreatedPoll created = polls.create(
                RequestContext.userId(request), chat, question, body == null ? null : body.options());
        realtime.messageCreated(chat, mapper.message(created.message()));
        realtime.pollUpdated(chat, created.poll());
        return ResponseEntity.status(HttpStatus.CREATED).body(created.poll());
    }

    @GetMapping("/{pollId}")
    public ApiDtos.PollDto get(
            @PathVariable String chatId,
            @PathVariable String pollId,
            HttpServletRequest request) {
        return polls.get(RequestContext.userId(request), parseChatId(chatId), pollId);
    }

    @PostMapping("/{pollId}/vote")
    public ApiDtos.PollDto vote(
            @PathVariable String chatId,
            @PathVariable String pollId,
            @RequestBody(required = false) ApiDtos.VotePollRequest body,
            HttpServletRequest request) {
        String optionId = body == null ? null : body.optionId();
        ApiDtos.PollDto poll = polls.vote(
                RequestContext.userId(request), parseChatId(chatId), pollId, optionId);
        realtime.pollUpdated(parseChatId(chatId), poll);
        return poll;
    }

    private static UUID parseChatId(String raw) {
        try {
            return UUID.fromString(raw);
        } catch (IllegalArgumentException e) {
            throw new ApiException(HttpStatus.BAD_REQUEST, "INVALID_ID", "Некорректный идентификатор чата");
        }
    }
}
