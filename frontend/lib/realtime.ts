'use client';

/**
 * UC-05 / mục 6.6 (Architecture v3.1): kết nối SignalR `/ticket-hub`, JWT qua query string
 * `access_token`, tự reconnect. Server quyết định group; client chỉ JoinTicket/LeaveTicket.
 */
import { useEffect, useRef } from 'react';
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { API_BASE, session } from '@/lib/api';
import type { TimelineEvent } from '@/lib/tickets';

let shared: HubConnection | null = null;
let starting: Promise<void> | null = null;

function connection(): HubConnection | null {
  const token = session.token();
  if (!token || typeof window === 'undefined') return null;
  if (shared) return shared;
  shared = new HubConnectionBuilder()
    .withUrl(`${API_BASE}/ticket-hub`, { accessTokenFactory: () => session.token() ?? '' })
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .configureLogging(LogLevel.Warning)
    .build();
  shared.onclose(() => {
    /* withAutomaticReconnect lo phần kết nối lại; khi token hết hạn, request 401 sẽ đưa về /login */
  });
  return shared;
}

async function ensureStarted(conn: HubConnection): Promise<void> {
  if (conn.state === HubConnectionState.Connected) return;
  if (!starting) {
    starting = conn.start().catch((err) => {
      starting = null;
      throw err;
    });
  }
  await starting;
}

export interface TicketChanged { ticketId: string; version: number; eventType: string }

/** Nhận event real-time của một ticket đang mở (group `ticket_{id}` / `_internal` do server gán). */
export function useTicketRealtime(ticketId: string | null, handlers: { onEvent?: (e: TimelineEvent) => void; onChanged?: (c: TicketChanged) => void }) {
  const ref = useRef(handlers);
  ref.current = handlers;

  useEffect(() => {
    if (!ticketId) return;
    const conn = connection();
    if (!conn) return;
    let joined = false;
    let disposed = false;

    const onEvent = (e: TimelineEvent) => { if (e.ticketId === ticketId) ref.current.onEvent?.(e); };
    const onChanged = (c: TicketChanged) => { if (c.ticketId === ticketId) ref.current.onChanged?.(c); };
    conn.on('ReceiveEvent', onEvent);
    conn.on('TicketChanged', onChanged);

    (async () => {
      try {
        await ensureStarted(conn);
        if (disposed) return;
        await conn.invoke('JoinTicket', ticketId);
        joined = true;
      } catch {
        /* real-time là tăng cường; trang vẫn hoạt động qua REST */
      }
    })();

    const rejoin = () => { if (!disposed) conn.invoke('JoinTicket', ticketId).catch(() => undefined); };
    conn.onreconnected(rejoin);

    return () => {
      disposed = true;
      conn.off('ReceiveEvent', onEvent);
      conn.off('TicketChanged', onChanged);
      if (joined && conn.state === HubConnectionState.Connected) conn.invoke('LeaveTicket', ticketId).catch(() => undefined);
    };
  }, [ticketId]);
}

/** Số thông báo chưa đọc (group `user_{id}`). */
export function useNotificationRealtime(onChanged: (unread: number) => void) {
  const ref = useRef(onChanged);
  ref.current = onChanged;
  useEffect(() => {
    const conn = connection();
    if (!conn) return;
    const handler = (m: { unreadCount: number }) => ref.current(m.unreadCount);
    conn.on('NotificationChanged', handler);
    ensureStarted(conn).catch(() => undefined);
    return () => conn.off('NotificationChanged', handler);
  }, []);
}

export function disconnectRealtime() {
  const conn = shared;
  shared = null;
  starting = null;
  conn?.stop().catch(() => undefined);
}
