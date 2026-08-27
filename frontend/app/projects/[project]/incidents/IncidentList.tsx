'use client';

import Link from 'next/link';
import { useParams } from 'next/navigation';
import { useCallback, useEffect, useState } from 'react';
import {
  api,
  formatTime,
  session,
  type Incident,
  type IncidentSeverity,
  type Paged,
} from '@/lib/api';
import { useAction } from '@/lib/useAction';
import { DATE_RANGES, rangeStartIso, type DateRange } from '@/lib/util';
import { ActionFeedback, ErrorBox, PageHead, Pager, RequiredMark, Select, SeverityBadge, SlaBadge, StatusBadge, SubmitButton, Toggle } from '@/components/ui';
import { Icons, UnassignedAvatar } from '@/components/tickets/Bits';
import { QueryInput } from '@/components/QueryInput';
import { tr } from '@/lib/i18n';

/**
 * UC-BIZ-05. Dùng chung cho màn hình "Tất cả sự cố" và "Việc của tôi"; bản ghi đã xóa mềm
 * không bao giờ xuất hiện vì API lọc sẵn bằng global query filter (ADR-003).
 */
export function IncidentList({ onlyMine = false }: { onlyMine?: boolean }) {
  // Sự cố thuộc về project; slug lấy từ đường dẫn để mọi lời gọi API đi đúng phạm vi.
  const { project } = useParams<{ project: string }>();
  const [data, setData] = useState<Paged<Incident> | null>(null);
  // latest: đổi bộ lọc ngay giữa lúc đang tải phải thắng lượt cũ, không bị nuốt.
  const load = useAction<Paged<Incident>>({ latest: true });
  const create = useAction<Incident>();

  // Ô tìm kiếm dùng lại đúng component và đúng lớp CSS của màn hình Issues, nên hai màn hình
  // không lệch nhau một pixel nào. Chữ tìm gửi lên server chứ không lọc trên mảng đã tải: lọc ở
  // trình duyệt chỉ lọc được đúng trang đang xem, còn con số tổng thì vẫn đếm cả phần không khớp.
  const [q, setQ] = useState('');
  const [status, setStatus] = useState('');
  const [severity, setSeverity] = useState('');
  const [createdRange, setCreatedRange] = useState<DateRange>('all');
  const [slaBreachedOnly, setSlaBreachedOnly] = useState(false);
  const [page, setPage] = useState(1);

  const [creating, setCreating] = useState(false);
  const [title, setTitle] = useState('');
  const [description, setDescription] = useState('');
  const [newSeverity, setNewSeverity] = useState<IncidentSeverity>('Medium');

  const canCreate = session.can('incident.create');

  // Khách hàng không có incident.read.all nên API chỉ trả về sự cố của chính họ. Nói rõ điều
  // đó trên giao diện, nếu không danh sách trống trông như hệ thống hỏng.
  const seesEverything = session.can('incident.read.all');

  const runLoad = useCallback(async () => {
    const result = await load.run(() =>
      api.listIncidents(project, {
        q: q || undefined,
        status: status || undefined,
        severity: severity || undefined,
        assigneeId: onlyMine ? (session.user()?.id ?? undefined) : undefined,
        createdFrom: rangeStartIso(createdRange),
        slaBreachedOnly: slaBreachedOnly || undefined,
        page,
        pageSize: 20,
      }),
    );
    if (result) {
      setData(result);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [q, status, severity, createdRange, slaBreachedOnly, page, onlyMine]);

  useEffect(() => {
    void runLoad();
  }, [runLoad]);

  async function createIncident(event: React.FormEvent) {
    event.preventDefault();
    const created = await create.run(
      () => api.createIncident(project, { title, description: description || undefined, severity: newSeverity }),
      tr('Đã ghi nhận sự cố ở trạng thái "Đang điều tra".'),
    );

    if (created) {
      setTitle('');
      setDescription('');
      setNewSeverity('Medium');
      setCreating(false);
      setPage(1);
      await runLoad();
    }
  }

  return (
    <>
      <PageHead
        kicker={onlyMine ? tr('Được giao cho tôi') : seesEverything ? tr('Toàn hệ thống') : tr('Của tôi')}
        title={onlyMine ? tr('Việc của tôi') : 'Incident'}
        hint={onlyMine
          ? tr('Các sự cố đang được giao cho bạn xử lý.')
          : seesEverything
            ? tr('Sự cố chưa bị xóa mềm, sắp xếp mới nhất trước.')
            : tr('Các sự cố bạn đã gửi, sắp xếp mới nhất trước.')}
        actions={canCreate && !onlyMine ? (
          <button type="button" className={creating ? 'btn btn-secondary' : 'btn btn-primary'} onClick={() => setCreating((v) => !v)}>
            {creating ? tr('Hủy') : seesEverything ? tr('Ghi nhận sự cố mới') : tr('Gửi sự cố mới')}
          </button>
        ) : undefined}
      />

      <ActionFeedback action={create} processingLabel={tr('Đang ghi nhận sự cố…')} />
      {load.status === 'failed' && <ErrorBox error={load.error} />}

      {creating && (
        <div className="card">
          <h2>{tr('Ghi nhận sự cố mới')}</h2>
          <p className="card-hint">
            {tr('Sự cố luôn được tạo ở trạng thái')} <strong>{tr('Đang điều tra')}</strong> {tr('và người ghi nhận lấy từ tài khoản đăng nhập — hai giá trị này không nhận từ biểu mẫu (BR-BIZ-01, BR-BIZ-04).')}
          </p>
          <form onSubmit={createIncident}>
            <div className="field">
              <label htmlFor="title">{tr('Tiêu đề (5–255 ký tự)')}<RequiredMark /></label>
              <input
                id="title"
                required
                minLength={5}
                maxLength={255}
                value={title}
                onChange={(e) => setTitle(e.target.value)}
              />
            </div>
            <div className="field">
              <label htmlFor="description">{tr('Mô tả và bước tái hiện')}</label>
              <textarea
                id="description"
                value={description}
                onChange={(e) => setDescription(e.target.value)}
              />
            </div>
            <div className="field">
              <label htmlFor="severity">{tr('Mức độ ảnh hưởng')}</label>
              <Select
                id="severity"
                value={newSeverity}
                onChange={(val) => setNewSeverity(val as IncidentSeverity)}
                options={[
                  { value: 'Low', label: tr('Thấp') },
                  { value: 'Medium', label: tr('Trung bình') },
                  { value: 'High', label: 'Cao' },
                  { value: 'Critical', label: tr('Nghiêm trọng') },
                ]}
              />
            </div>
            <SubmitButton action={create} processingLabel={tr('Đang tạo…')} className="btn btn-primary">
              {tr('Tạo sự cố')}
            </SubmitButton>
          </form>
        </div>
      )}

      <div className="list-section">
        <div className="issues-toolbar" style={{ marginBottom: 16 }}>
          <QueryInput
            className="search"
            value={q}
            onSubmit={(next) => { setQ(next.trim()); setPage(1); }}
            prefix={Icons.search}
            placeholder={tr('Tìm sự cố')}
            ariaLabel={tr('Tìm sự cố')}
          />
        </div>

        <div className="row" style={{ marginBottom: 16 }}>
          <div className="field">
            <label htmlFor="f-status">{tr('Trạng thái')}</label>
            <Select
              id="f-status"
              value={status}
              onChange={(val) => {
                setStatus(val);
                setPage(1);
              }}
              options={[
                { value: '', label: tr('Tất cả') },
                { value: 'Investigating', label: tr('Đang điều tra') },
                { value: 'Mitigating', label: tr('Đang khắc phục') },
                { value: 'Resolved', label: tr('Đã giải quyết') },
              ]}
            />
          </div>
          <div className="field">
            <label htmlFor="f-severity">{tr('Mức độ')}</label>
            <Select
              id="f-severity"
              value={severity}
              onChange={(val) => {
                setSeverity(val);
                setPage(1);
              }}
              options={[
                { value: '', label: tr('Tất cả') },
                { value: 'Low', label: tr('Thấp') },
                { value: 'Medium', label: tr('Trung bình') },
                { value: 'High', label: 'Cao' },
                { value: 'Critical', label: tr('Nghiêm trọng') },
              ]}
            />
          </div>
          <div className="field">
            <label htmlFor="f-from">{tr('Tạo trong')}</label>
            <Select
              id="f-from"
              value={createdRange}
              onChange={(next) => {
                setCreatedRange(next as DateRange);
                setPage(1);
              }}
              options={DATE_RANGES.map((r) => ({ value: r.value, label: tr(r.label) }))}
            />
          </div>
          <div className="field" style={{ flex: '0 0 auto' }}>
            <label>Breached</label>
            {/* Công tắc chứ không phải ô tick: đây là bộ lọc có hiệu lực ngay, không phải một
                trường trong biểu mẫu chờ bấm Lưu. */}
            <div style={{ display: 'flex', alignItems: 'center', minHeight: 'var(--control-h)' }}>
              <Toggle
                checked={slaBreachedOnly}
                ariaLabel={tr('Chỉ sự cố quá hạn SLA')}
                onChange={(on) => {
                  setSlaBreachedOnly(on);
                  setPage(1);
                }}
              />
            </div>
          </div>
        </div>

        {load.isProcessing && <div className="empty">{tr('Đang tải danh sách sự cố…')}</div>}

        {!load.isProcessing && data && data.items.length === 0 && (
          <div className="empty">{tr('Không có sự cố nào khớp bộ lọc.')}</div>
        )}

        {!load.isProcessing && data && data.items.length > 0 && (
          <>
            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>{tr('Tiêu đề')}</th>
                    <th>{tr('Trạng thái')}</th>
                    <th>{tr('Mức độ')}</th>
                    <th>SLA</th>
                    <th>{tr('Người xử lý')}</th>
                    <th>{tr('Ghi nhận lúc')}</th>
                  </tr>
                </thead>
                <tbody>
                  {data.items.map((incident) => (
                    <tr key={incident.id}>
                      <td>
                        <Link href={`/projects/${project}/incidents/${incident.id}`}>{incident.title}</Link>
                        <div className="muted">Người ghi nhận: {incident.reporter.displayName}</div>
                      </td>
                      <td>
                        <StatusBadge status={incident.status} />
                      </td>
                      <td>
                        <SeverityBadge severity={incident.severity} />
                      </td>
                      <td>
                        <SlaBadge sla={incident.sla} />
                      </td>
                      <td>{incident.assignee?.displayName ?? <UnassignedAvatar size="sm" withText />}</td>
                      <td className="muted">{formatTime(incident.createdAt)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <Pager
              page={data.page}
              pageSize={data.pageSize}
              totalCount={data.totalCount}
              onChange={setPage}
            />
          </>
        )}
      </div>
    </>
  );
}
