'use client';

import Link from 'next/link';
import { useParams } from 'next/navigation';
import { useCallback, useEffect, useState } from 'react';
import { ErrorBox, Guard, PageHead, Select } from '@/components/ui';
import { Avatar, LabelChip, NoLabelChip, UnassignedAvatar } from '@/components/tickets/Bits';
import { session } from '@/lib/api';
import { tickets, type BoardDetail, type BoardItem } from '@/lib/tickets';
import { tr } from '@/lib/i18n';

/** Board Kanban: cột = BoardColumn, thẻ = BoardItem; HTML5 drag & drop → PATCH items/{id} (columnId, position). */
function BoardView() {
  const { id } = useParams<{ id: string }>();
  const [detail, setDetail] = useState<BoardDetail | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [dragging, setDragging] = useState<string | null>(null);
  const [over, setOver] = useState<string | null>(null);
  const [addTo, setAddTo] = useState<{ columnId: string; text: string } | null>(null);
  const [newCol, setNewCol] = useState('');
  // Sửa tên cột tại chỗ, và kéo cột để đổi thứ tự. Hai trạng thái tách khỏi `dragging` của thẻ
  // vì hai loại kéo dùng chung một vùng thả — lẫn nhau là thả thẻ vào chỗ đổi thứ tự cột.
  const [editCol, setEditCol] = useState<{ id: string; name: string } | null>(null);
  const [dragCol, setDragCol] = useState<string | null>(null);
  const [colNotice, setColNotice] = useState<string | null>(null);
  const [autoQuery, setAutoQuery] = useState<string>('');
  const canWrite = session.can('board.write');

  const load = useCallback(() => tickets.board(id).then((d) => { setDetail(d); setAutoQuery(d.board.automation.auto_add_query ?? ''); }).catch(setError), [id]);
  useEffect(() => { void load(); }, [load]);

  if (error && !detail) return <ErrorBox error={error} />;
  if (!detail) return <div className="empty">{tr('Đang tải…')}</div>;
  const { board, items } = detail;
  const columns = [...board.columns].sort((a, b) => a.position - b.position);
  const noColumn = items.filter((i) => !i.columnId);

  async function drop(columnId: string | null, position: number) {
    // Đang kéo cột thì mọi vùng thả của thẻ phải im lặng, nếu không thả cột xuống giữa danh sách
    // thẻ sẽ bị hiểu là chuyển thẻ.
    if (dragCol) return;
    if (!dragging || !canWrite) return;
    const itemId = dragging; setDragging(null); setOver(null);
    setDetail((d) => d && { ...d, items: d.items.map((i) => (i.id === itemId ? { ...i, columnId, position } : i)) });
    try { await tickets.moveBoardItem(id, itemId, { columnId: columnId ?? undefined, position }); await load(); } catch (e) { setError(e); await load(); }
  }

  /**
   * Đổi thứ tự cột.
   *
   * Đánh lại vị trí cả dãy thành 0..n-1 rồi chỉ gửi những cột thực sự đổi. Vị trí trong cơ sở dữ
   * liệu không nhất thiết liền mạch (thêm cột dùng `max + 1`, xóa cột để lại lỗ hổng), nên chuẩn
   * hoá lại khiến lần kéo sau không phải đoán khoảng trống còn bao nhiêu.
   */
  async function moveColumn(columnId: string, toIndex: number) {
    if (!canWrite) return;
    const order = columns.map((c) => c.id);
    const from = order.indexOf(columnId);
    if (from < 0 || from === toIndex) return;
    order.splice(toIndex, 0, ...order.splice(from, 1));
    try {
      for (let i = 0; i < order.length; i += 1) {
        const col = columns.find((c) => c.id === order[i])!;
        if (col.position !== i) await tickets.updateColumn(id, col.id, { name: col.name, position: i });
      }
      await load();
    } catch (e) { setError(e); await load(); }
  }

  async function renameColumn() {
    if (!editCol) return;
    const name = editCol.name.trim();
    const col = columns.find((c) => c.id === editCol.id);
    setEditCol(null);
    if (!col || !name || name === col.name) return;
    try { await tickets.updateColumn(id, col.id, { name, position: col.position }); await load(); }
    catch (e) { setError(e); }
  }

  /**
   * Xóa cột — chỉ khi cột đã trống.
   *
   * Kiểm ở đây để nói lý do ngay tại chỗ thay vì để người dùng bấm rồi đọc lỗi đỏ; backend vẫn
   * trả 409 vì endpoint gọi thẳng được, và hai chỗ phải nói cùng một điều.
   */
  async function removeColumn(columnId: string, name: string, count: number) {
    setColNotice(null);
    if (count > 0) {
      setColNotice(tr('Cột "{v0}" còn {v1} thẻ. Chuyển hết sang cột khác rồi mới xóa được.', { v0: name, v1: count }));
      return;
    }
    if (!confirm(tr('Xóa cột "{v0}"?', { v0: name }))) return;
    try { await tickets.deleteColumn(id, columnId); await load(); } catch (e) { setError(e); }
  }

  function Card({ item }: { item: BoardItem }) {
    const t = item.ticket;
    const closed = t?.state === 'CLOSED';
    // Bản mẫu làm nổi đúng một thẻ bằng vạch accent bên trái. Ở đây nó đánh dấu thẻ đang kéo.
    const urgent = dragging === item.id;
    return (
      <div className={`board-card${urgent ? ' accent' : ''}${closed ? ' done' : ''}`} draggable={canWrite}
        onDragStart={() => setDragging(item.id)} onDragEnd={() => { setDragging(null); setOver(null); }}>
        {t ? (
          <>
            <span className="num">{t.projectSlug} #{t.number}</span>
            <Link href={`/projects/${t.projectSlug}/issues/${t.number}`} className="title">{t.title}</Link>
          </>
        ) : (
          <>
            <span className="num">Draft item</span>
            <span className="title">{item.draftTitle}</span>
          </>
        )}
        <div className="labels">
          {closed && <span className="tag tag-neutral">{(t?.stateReason ?? 'COMPLETED').toLowerCase().replace('_', ' ')}</span>}
          {item.labels.length === 0 ? <NoLabelChip /> : item.labels.map((l) => <LabelChip key={l.id} label={l} />)}
        </div>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 4 }}>
          <span style={{ display: 'flex', gap: 4 }}>
            {item.assignees.length === 0
              ? <UnassignedAvatar size="sm" />
              : item.assignees.map((a) => <Avatar key={a.id} user={a} size="sm" />)}
          </span>
          {canWrite && <button type="button" className="btn btn-ghost" style={{ fontSize: 12 }} onClick={() => tickets.removeBoardItem(id, item.id).then(load).catch(setError)}>×</button>}
        </div>
      </div>
    );
  }

  function Column({ columnId, name, list }: { columnId: string | null; name: string; list: BoardItem[] }) {
    const sorted = [...list].sort((a, b) => a.position - b.position);
    const key = columnId ?? '__none';
    return (
      <div className={`board-col ${over === key ? 'over' : ''}${dragCol && columnId && dragCol !== columnId ? ' col-target' : ''}`}
        onDragOver={(e) => {
          e.preventDefault();
          // Kéo cột thì không tô sáng vùng thả thẻ — hai loại kéo phải nhìn khác nhau, nếu không
          // người dùng tưởng mình sắp thả một thẻ vào đây.
          setOver(dragCol ? null : key);
        }}
        onDragLeave={() => setOver(null)}
        onDrop={(e) => {
          e.preventDefault();
          if (dragCol) {
            // Cả cột là vùng thả, không riêng dòng tiêu đề: thả trúng một dòng cao 20px là yêu
            // cầu quá đáng, và thả hụt thì không có phản hồi nào để hiểu là đã trượt.
            if (!columnId || dragCol === columnId) { setDragCol(null); return; }
            const to = columns.findIndex((c) => c.id === columnId);
            const moving = dragCol;
            setDragCol(null);
            void moveColumn(moving, to);
            return;
          }
          void drop(columnId, sorted.length);
        }}>
        <div
          className={`board-col-head${over === key ? ' active' : ''}${dragCol === columnId ? ' dragging' : ''}`}
          // Chỉ đầu cột kéo được: thẻ bên trong có `draggable` riêng, để cả cột kéo được thì
          // không còn cách nào cầm một thẻ lên.
          draggable={canWrite && Boolean(columnId) && !editCol}
          onDragStart={(e) => { if (columnId) { e.stopPropagation(); setDragCol(columnId); } }}
          onDragEnd={() => setDragCol(null)}
        >
          {editCol?.id === columnId ? (
            <form style={{ flex: 1 }} onSubmit={(e) => { e.preventDefault(); void renameColumn(); }}>
              <input autoFocus className="input" value={editCol.name} aria-label={tr('Tên cột')}
                style={{ minHeight: 'var(--control-h-sm)', fontSize: 13 }}
                onChange={(e) => setEditCol({ id: editCol.id, name: e.target.value })}
                onBlur={() => void renameColumn()}
                onKeyDown={(e) => { if (e.key === 'Escape') setEditCol(null); }} />
            </form>
          ) : (
            <span className="grow">{name} <span className="pill-count">{sorted.length}</span></span>
          )}
          {columnId && board.automation.item_closed_to_column_id === columnId && <span className="muted" title={tr('Ticket đóng sẽ tự chuyển vào cột này')} style={{ fontSize: 11 }}>auto: closed</span>}
          {canWrite && columnId && editCol?.id !== columnId && (
            <>
              <button type="button" className="btn btn-ghost col-act" title={tr('Đổi tên cột')}
                onClick={() => setEditCol({ id: columnId, name })}>✎</button>
              <button type="button" className="btn btn-ghost col-act" title={tr('Xóa cột')}
                onClick={() => void removeColumn(columnId, name, sorted.length)}>×</button>
            </>
          )}
        </div>
        <div className="board-col-body">
          {sorted.map((item, idx) => (
            <div key={item.id}
              onDragOver={(e) => { if (dragCol) return; e.preventDefault(); e.stopPropagation(); setOver(key); }}
              onDrop={(e) => { if (dragCol) return; e.preventDefault(); e.stopPropagation(); void drop(columnId, idx); }}>
              <Card item={item} />
            </div>
          ))}
          {canWrite && columnId && (
            addTo?.columnId === columnId ? (
              <form onSubmit={(e) => { e.preventDefault(); const t = addTo.text.trim(); if (!t) return; const body = /^(\w[\w-]*)?#\d+$/.test(t) ? { ticket: t, columnId } : { draftTitle: t, columnId }; tickets.addBoardItem(id, body).then(() => { setAddTo(null); void load(); }).catch(setError); }}>
                <input autoFocus placeholder={tr('#123, support#7 hoặc tiêu đề draft')} value={addTo.text} onChange={(e) => setAddTo({ columnId, text: e.target.value })} onBlur={() => !addTo.text && setAddTo(null)} />
              </form>
            ) : <button type="button" className="btn btn-secondary btn-block" style={{ marginTop: 0, color: 'var(--color-accent)' }} onClick={() => setAddTo({ columnId, text: '' })}>+ Add item</button>
          )}
        </div>
      </div>
    );
  }

  return (
    <>
      <PageHead
        kicker={tr('Board · {v0} thẻ', { v0: items.length })}
        title={board.name}
        actions={<>
          <Link href="/boards" className="btn btn-secondary">{tr('Tất cả board')}</Link>
          {canWrite && (
            <>
              <form style={{ display: 'flex', gap: 'var(--space-2)' }} onSubmit={(e) => { e.preventDefault(); if (newCol.trim()) tickets.addColumn(id, newCol).then(() => { setNewCol(''); void load(); }).catch(setError); }}>
                <input className="input" placeholder={tr('Cột mới')} value={newCol} onChange={(e) => setNewCol(e.target.value)} style={{ width: 140 }} />
                <button type="submit" className="btn btn-secondary">{tr('+ Cột')}</button>
              </form>
              <button type="button" className="btn btn-secondary" onClick={() => tickets.updateBoard(id, { name: board.name, description: board.description ?? undefined, isClosed: !board.isClosed }).then(load).catch(setError)}>{board.isClosed ? tr('Mở lại board') : tr('Đóng board')}</button>
            </>
          )}
        </>}
      />

      {/* Dòng automation của bản mẫu: kẻ trên, chữ nhỏ, truy vấn đặt trong <code>. */}
      <div style={{ fontSize: 12, color: 'color-mix(in srgb, var(--color-text) 60%, transparent)', borderTop: '1px solid var(--color-divider)', paddingTop: 'var(--space-2)', display: 'flex', gap: 'var(--space-2)', alignItems: 'center', flexWrap: 'wrap' }}>
        {board.description && <span>{board.description} ·</span>}
        <span>Auto-add</span>
        {canWrite ? (
          <input className="input" style={{ width: 260, minHeight: 'var(--control-h-sm)', fontFamily: 'var(--font-mono)', fontSize: 12 }}
            placeholder="is:open label:incident" value={autoQuery} onChange={(e) => setAutoQuery(e.target.value)}
            onBlur={() => autoQuery !== (board.automation.auto_add_query ?? '') && tickets.updateBoard(id, { name: board.name, description: board.description ?? undefined, automation: { ...board.automation, auto_add_query: autoQuery || null } }).then(load).catch(setError)} />
        ) : <code>{board.automation.auto_add_query || '—'}</code>}
        <span>{tr('· ticket đóng chuyển sang')}</span>
        {canWrite ? (
          <Select
            inline
            value={board.automation.item_closed_to_column_id ?? ''}
            placeholder={tr('không tự chuyển')}
            ariaLabel={tr('· ticket đóng chuyển sang')}
            onChange={(columnId) => tickets.updateBoard(id, { name: board.name, description: board.description ?? undefined, automation: { ...board.automation, item_closed_to_column_id: columnId || undefined } }).then(load).catch(setError)}
            options={[{ value: '', label: tr('không tự chuyển') }, ...columns.map((c) => ({ value: c.id, label: c.name }))]}
          />
        ) : <code>{columns.find((c) => c.id === board.automation.item_closed_to_column_id)?.name ?? '—'}</code>}
      </div>
      {error ? <ErrorBox error={error} /> : null}
      {colNotice && (
        <div className="alert error" role="alert">
          {colNotice} <button type="button" className="btn btn-ghost" onClick={() => setColNotice(null)}>✕</button>
        </div>
      )}
      <div className="board">
        {noColumn.length > 0 && <Column columnId={null} name={tr('Chưa phân cột')} list={noColumn} />}
        {columns.map((c) => <Column key={c.id} columnId={c.id} name={c.name} list={items.filter((i) => i.columnId === c.id)} />)}
        {columns.length === 0 && <div className="empty">{tr('Chưa có cột — thêm cột để bắt đầu (ví dụ Todo / In progress / Done).')}</div>}
      </div>
    </>
  );
}

export default function BoardPage() { return <Guard permission="ticket.read"><BoardView /></Guard>; }
