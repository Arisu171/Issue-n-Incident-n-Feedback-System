'use client';

import React, { useEffect, useState } from 'react';
import { s } from '@/lib/style';

type Screen =
  | 'issues' | 'ticket' | 'new' | 'board' | 'inbox'
  | 'search' | 'labels' | 'settings' | 'rbac' | 'sla';

const NAV: [Screen, string][] = [
  ['issues', 'Issues'], ['ticket', 'Ticket'], ['new', 'New issue'], ['board', 'Board'],
  ['inbox', 'Inbox'], ['search', 'Search'], ['labels', 'Labels'], ['settings', 'Settings'],
  ['rbac', 'RBAC'], ['sla', 'SLA'],
];

const NAV_BASE =
  'appearance:none;cursor:pointer;background:transparent;border:0;border-bottom:2px solid transparent;' +
  'margin-bottom:-2px;padding:15px 16px;font-family:var(--font-heading);font-weight:800;font-size:12px;' +
  'letter-spacing:.04em;text-transform:uppercase;';

export default function TicketSystem() {
  const [screen, setScreen] = useState<Screen>('issues');
  const [theme, setTheme] = useState<'light' | 'dark'>('light');
  const [narrow, setNarrow] = useState(false);
  const [blank, setBlank] = useState(true);
  const [strict, setStrict] = useState(false);
  const [reopen, setReopen] = useState(false);
  const [priority, setPriority] = useState<'P0' | 'P1' | 'P2' | 'P3'>('P0');

  useEffect(() => {
    document.documentElement.dataset.theme = theme;
  }, [theme]);

  const nav = NAV.map(([id, label]) => ({
    label,
    go: () => setScreen(id),
    style:
      NAV_BASE +
      (screen === id
        ? 'color:var(--color-accent);border-bottom-color:var(--color-accent);'
        : 'color:color-mix(in srgb, var(--color-text) 60%, transparent);'),
  }));

  const isIssues = screen === 'issues';
  const isTicket = screen === 'ticket';
  const isNew = screen === 'new';
  const isBoard = screen === 'board';
  const isInbox = screen === 'inbox';
  const isSearch = screen === 'search';
  const isLabels = screen === 'labels';
  const isSettings = screen === 'settings';
  const isRbac = screen === 'rbac';
  const isSla = screen === 'sla';

  const isLight = theme === 'light';
  const isDark = theme === 'dark';
  const themeLabel = isLight ? 'Dark' : 'Light';
  const viewportLabel = narrow ? 'Desktop' : 'Mobile';

  const toggleTheme = () => setTheme((t) => (t === 'light' ? 'dark' : 'light'));
  const toggleNarrow = () => setNarrow((v) => !v);
  const goTicket = (e?: React.MouseEvent) => {
    if (e) e.preventDefault();
    setScreen('ticket');
  };
  const goNew = () => setScreen('new');
  const goInbox = () => setScreen('inbox');

  const blankIssues = blank;
  const strictClose = strict;
  const autoReopen = reopen;
  const setBlankIssues = () => setBlank((v) => !v);
  const setStrictClose = () => setStrict((v) => !v);
  const setAutoReopen = () => setReopen((v) => !v);

  const isP0 = priority === 'P0';
  const isP1 = priority === 'P1';
  const isP2 = priority === 'P2';
  const isP3 = priority === 'P3';
  const pickP0 = () => setPriority('P0');
  const pickP1 = () => setPriority('P1');
  const pickP2 = () => setPriority('P2');
  const pickP3 = () => setPriority('P3');
  const priorityNote =
    isP0 ? 'P0 · first response within 15 minutes'
      : isP1 ? 'P1 · first response within 1 hour'
        : isP2 ? 'P2 · first response within 4 hours'
          : 'P3 · first response within 1 day';

  const frameStyle = narrow
    ? 'max-width:390px;margin:0 auto;border-left:1px solid var(--color-divider);border-right:1px solid var(--color-divider);'
    : 'max-width:1240px;margin:0 auto;';
  const formGrid = narrow
    ? 'display:grid;gap:var(--space-3);grid-template-columns:minmax(0,1fr);'
    : 'display:grid;gap:var(--space-3);grid-template-columns:3fr 1fr;align-items:start;';
  const detailGrid = narrow
    ? 'display:grid;gap:var(--space-4);grid-template-columns:minmax(0,1fr);'
    : 'display:grid;gap:var(--space-6);grid-template-columns:minmax(0,1fr) 300px;align-items:start;';

  return (
    <>
      <div style={s("min-height:100vh;background:var(--color-bg);color:var(--color-text);font-family:var(--font-body)")}>
      
        <header style={s("display:flex;align-items:center;gap:var(--space-4);padding:var(--space-3) var(--space-4);border-bottom:1px solid var(--color-divider);flex-wrap:nowrap")}>
          <div style={s("flex:1 1 auto;min-width:min-content;display:flex;align-items:center;gap:var(--space-4)")}>
          <div style={s("font-family:var(--font-heading);font-weight:800;font-size:15px;letter-spacing:-0.01em;white-space:nowrap")}>X.OS</div>
          <div style={s("display:flex;align-items:center;gap:var(--space-2);font-size:13px")}>
            <span style={s("color:color-mix(in srgb, var(--color-text) 50%, transparent)")}>/</span>
            <select className="input dd" style={s("width:auto;height:34px;min-height:34px;padding:2px 8px;font-size:13px")}>
              <option>payments-core</option>
              <option>web-platform</option>
              <option>customer-support</option>
            </select>
          </div>
          </div>
          <div style={s("flex:0 1 520px;min-width:0;height:34px;display:flex;align-items:center;gap:6px;border:1px solid var(--color-divider);border-radius:var(--radius-md);background:var(--color-surface);padding:0 8px")}>
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" style={s("opacity:.55;flex:none")}><circle cx="11" cy="11" r="8"></circle><path d="m21 21-4.3-4.3"></path></svg>
            <input className="input" style={s("border:0;background:transparent;min-height:32px;padding:4px 0;font-size:13px")} defaultValue="is:open label:incident assignee:@me" aria-label="Search tickets" />
          </div>
          <div style={s("flex:1 1 auto;min-width:min-content;display:flex;align-items:center;justify-content:flex-end;gap:var(--space-2)")}>
            <button type="button" className="btn btn-secondary" onClick={toggleNarrow} style={s("height:34px;font-size:11px;letter-spacing:.06em")}>{viewportLabel}</button>
            <button type="button" className="btn btn-secondary btn-icon" onClick={toggleTheme} style={s("flex:none")} aria-label={themeLabel}>
              {isLight && (<>
                <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><circle cx="12" cy="12" r="9" fill="currentColor" stroke="none"></circle></svg>
              </>)}
              {isDark && (<>
                <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><path d="M20 14.5A8.5 8.5 0 1 1 9.5 4a6.8 6.8 0 0 0 10.5 10.5z" fill="currentColor" stroke="none"></path></svg>
              </>)}
            </button>
            <span style={s("position:relative;display:inline-flex")}>
              <button type="button" className="btn btn-secondary btn-icon" onClick={goInbox} aria-label="Notifications">
                <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><path d="M22 12h-6l-2 3h-4l-2-3H2"></path><path d="M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z"></path></svg>
              </button>
              <span style={s("position:absolute;top:-4px;right:-4px;background:var(--color-accent);color:var(--color-bg);font-family:var(--font-heading);font-weight:800;font-size:10px;line-height:1;padding:3px 4px")}>7</span>
            </span>
            <span style={s("width:34px;height:34px;display:grid;place-items:center;border-radius:var(--radius-sm);flex:none;background:var(--color-text);color:var(--color-bg);font-family:var(--font-heading);font-weight:800;font-size:12px")}>L</span>
          </div>
        </header>
      
        <nav className="menubar" style={s("border-bottom:1px solid var(--color-divider)")}>
          <div style={s("display:flex;align-items:stretch;gap:0;width:max-content;margin:0 auto")}>
            {nav.map((item, i) => (<button key={i} type="button" onClick={item.go} style={s(item.style)}>{item.label}</button>))}
          </div>
        </nav>
      
        <main style={s(frameStyle)}>
      
          {isIssues && (<>
            <section style={s("padding:var(--space-6) var(--space-4);display:flex;flex-direction:column;gap:var(--space-4)")}>
              <div style={s("display:flex;align-items:flex-end;gap:var(--space-4);flex-wrap:wrap")}>
                <div>
                  <div style={s("font-size:11px;letter-spacing:.1em;text-transform:uppercase;color:var(--color-accent)")}>payments-core</div>
                  <h2 style={s("margin:0")}>Issues</h2>
                </div>
                <div style={s("margin-left:auto;display:flex;gap:var(--space-2);flex-wrap:wrap")}>
                  <button type="button" className="btn btn-secondary" style={s("height:38px")}>Labels</button>
                  <button type="button" className="btn btn-secondary" style={s("height:38px")}>Milestones</button>
                  <button type="button" className="btn btn-primary" style={s("height:38px")} onClick={goNew}>New issue</button>
                </div>
              </div>
      
              <div style={s("display:flex;gap:var(--space-2);flex-wrap:wrap;align-items:center;margin-bottom:var(--space-2)")}>
                <div style={s("flex:1;min-width:220px;height:38px;display:flex;align-items:center;gap:6px;border:1px solid var(--color-divider);border-radius:var(--radius-md);background:var(--color-surface);padding:0 10px")}>
                  <span style={s("font-family:var(--font-heading);font-weight:800;font-size:11px;letter-spacing:.08em;color:var(--color-accent)")}>DSL</span>
                  <input className="input" style={s("border:0;background:transparent;font-size:13px")} defaultValue="is:open label:incident sort:updated-desc" aria-label="Query DSL" />
                </div>
                <button type="button" className="btn btn-secondary" style={s("height:38px")}>Label<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" style={s("opacity:.6;margin-left:2px")}><path d="m6 9 6 6 6-6"></path></svg></button>
                <button type="button" className="btn btn-secondary" style={s("height:38px")}>Milestone<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" style={s("opacity:.6;margin-left:2px")}><path d="m6 9 6 6 6-6"></path></svg></button>
                <button type="button" className="btn btn-secondary" style={s("height:38px")}>Assignee<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" style={s("opacity:.6;margin-left:2px")}><path d="m6 9 6 6 6-6"></path></svg></button>
                <button type="button" className="btn btn-secondary" style={s("height:38px")}>Sort<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" style={s("opacity:.6;margin-left:2px")}><path d="m6 9 6 6 6-6"></path></svg></button>
              </div>
      
              <div style={s("display:flex;gap:var(--space-4);align-items:center;font-family:var(--font-heading);font-weight:800;font-size:13px")}>
                <span style={s("display:inline-flex;align-items:center;gap:6px;color:var(--color-accent);border-bottom:2px solid var(--color-accent);padding-bottom:4px")}>
                  <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><circle cx="12" cy="12" r="10"></circle><circle cx="12" cy="12" r="1" fill="currentColor"></circle></svg>
                  42 Open
                </span>
                <span style={s("display:inline-flex;align-items:center;gap:6px;color:color-mix(in srgb, var(--color-text) 55%, transparent);padding-bottom:4px")}>
                  <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><path d="M21.8 10A10 10 0 1 1 17 3.34"></path><path d="m9 11 3 3L22 4"></path></svg>
                  128 Closed
                </span>
              </div>
      
              <div style={s("border-top:1px solid var(--color-divider)")}>
                <div style={s("display:flex;gap:var(--space-3);padding:var(--space-3) 0;border-bottom:1px solid var(--color-divider);align-items:flex-start")}>
                  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="var(--color-accent)" strokeWidth="2" style={s("margin-top:3px;flex:none")}><circle cx="12" cy="12" r="10"></circle><circle cx="12" cy="12" r="1" fill="var(--color-accent)"></circle></svg>
                  <div style={s("flex:1;min-width:0;display:flex;flex-direction:column;gap:6px")}>
                    <div style={s("display:flex;gap:var(--space-2);flex-wrap:wrap;align-items:center")}>
                      <a href="#" onClick={goTicket} style={s("font-family:var(--font-heading);font-weight:800;font-size:16px;color:var(--color-text)")}>Payment settlement stuck for EU merchants</a>
                      <span className="tag tag-accent">incident</span>
                      <span className="tag tag-neutral">billing</span>
                    </div>
                    <div style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>#142 opened 2 hours ago by <strong>bao.long</strong> · Milestone v3.1 · Bug · 3 sub-issues (1/3)</div>
                  </div>
                  <div style={s("display:flex;align-items:center;gap:var(--space-3);flex:none;align-self:center")}>
                    <span className="tag tag-accent" style={s("font-family:var(--font-heading);font-weight:800")}>P0 · SLA 12m</span>
                    <span style={s("width:24px;height:24px;display:grid;place-items:center;border-radius:var(--radius-sm);flex:none;background:var(--color-text);color:var(--color-bg);font-size:10px;font-weight:800")}>L</span>
                    <span style={s("display:inline-flex;align-items:center;gap:4px;font-size:12px;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>
                      <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"></path></svg>14</span>
                  </div>
                </div>
      
                <div style={s("display:flex;gap:var(--space-3);padding:var(--space-3) 0;border-bottom:1px solid var(--color-divider);align-items:flex-start")}>
                  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="var(--color-accent)" strokeWidth="2" style={s("margin-top:3px;flex:none")}><circle cx="12" cy="12" r="10"></circle><circle cx="12" cy="12" r="1" fill="var(--color-accent)"></circle></svg>
                  <div style={s("flex:1;min-width:0;display:flex;flex-direction:column;gap:6px")}>
                    <div style={s("display:flex;gap:var(--space-2);flex-wrap:wrap;align-items:center")}>
                      <a href="#" onClick={goTicket} style={s("font-family:var(--font-heading);font-weight:800;font-size:16px;color:var(--color-text)")}>Webhook deliveries failing with 401 after key rotation</a>
                      <span className="tag tag-neutral">integration</span>
                      <span className="tag tag-outline">blocked</span>
                    </div>
                    <div style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>#139 opened yesterday by <strong>huy.kien</strong> · blocked by #133</div>
                  </div>
                  <div style={s("display:flex;align-items:center;gap:var(--space-3);flex:none;align-self:center")}>
                    <span className="tag tag-neutral" style={s("font-family:var(--font-heading);font-weight:800")}>P1</span>
                    <span style={s("width:24px;height:24px;display:grid;place-items:center;border-radius:var(--radius-sm);flex:none;background:var(--color-text);color:var(--color-bg);font-size:10px;font-weight:800")}>H</span>
                    <span style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>6</span>
                  </div>
                </div>
      
                <div style={s("display:flex;gap:var(--space-3);padding:var(--space-3) 0;border-bottom:1px solid var(--color-divider);align-items:flex-start")}>
                  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="var(--color-accent)" strokeWidth="2" style={s("margin-top:3px;flex:none")}><circle cx="12" cy="12" r="10"></circle><circle cx="12" cy="12" r="1" fill="var(--color-accent)"></circle></svg>
                  <div style={s("flex:1;min-width:0;display:flex;flex-direction:column;gap:6px")}>
                    <div style={s("display:flex;gap:var(--space-2);flex-wrap:wrap;align-items:center")}>
                      <a href="#" onClick={goTicket} style={s("font-family:var(--font-heading);font-weight:800;font-size:16px;color:var(--color-text)")}>Refund request not reflected in customer portal</a>
                      <span className="tag tag-neutral">feedback</span>
                    </div>
                    <div style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>#137 opened 2 days ago by <strong>customer/ngoc.mai</strong> · first-time contributor</div>
                  </div>
                  <div style={s("display:flex;align-items:center;gap:var(--space-3);flex:none;align-self:center")}>
                    <span className="tag tag-neutral" style={s("font-family:var(--font-heading);font-weight:800")}>P2</span>
                    <span style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 45%, transparent)")}>unassigned</span>
                    <span style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>2</span>
                  </div>
                </div>
      
                <div style={s("display:flex;gap:var(--space-3);padding:var(--space-3) 0;border-bottom:1px solid var(--color-divider);align-items:flex-start")}>
                  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" style={s("margin-top:3px;flex:none;opacity:.5")}><path d="M21.8 10A10 10 0 1 1 17 3.34"></path><path d="m9 11 3 3L22 4"></path></svg>
                  <div style={s("flex:1;min-width:0;display:flex;flex-direction:column;gap:6px")}>
                    <div style={s("display:flex;gap:var(--space-2);flex-wrap:wrap;align-items:center")}>
                      <a href="#" onClick={goTicket} style={s("font-family:var(--font-heading);font-weight:800;font-size:16px;color:color-mix(in srgb, var(--color-text) 70%, transparent)")}>Latency spike on /api/search/tickets</a>
                      <span className="tag tag-neutral">performance</span>
                    </div>
                    <div style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>#128 closed as completed 3 days ago by <strong>son.truong</strong></div>
                  </div>
                  <div style={s("display:flex;align-items:center;gap:var(--space-3);flex:none;align-self:center")}>
                    <span style={s("width:24px;height:24px;display:grid;place-items:center;border-radius:var(--radius-sm);flex:none;background:color-mix(in srgb, var(--color-text) 65%, transparent);color:var(--color-bg);font-size:10px;font-weight:800")}>S</span>
                    <span style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>9</span>
                  </div>
                </div>
      
                <div style={s("display:flex;gap:var(--space-3);padding:var(--space-3) 0;border-bottom:1px solid var(--color-divider);align-items:flex-start")}>
                  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" style={s("margin-top:3px;flex:none;opacity:.5")}><circle cx="12" cy="12" r="10"></circle><path d="m4.9 4.9 14.2 14.2"></path></svg>
                  <div style={s("flex:1;min-width:0;display:flex;flex-direction:column;gap:6px")}>
                    <div style={s("display:flex;gap:var(--space-2);flex-wrap:wrap;align-items:center")}>
                      <a href="#" onClick={goTicket} style={s("font-family:var(--font-heading);font-weight:800;font-size:16px;color:color-mix(in srgb, var(--color-text) 70%, transparent)")}>Add dark theme to customer portal</a>
                      <span className="tag tag-neutral">wontfix</span>
                    </div>
                    <div style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>#121 closed as not planned last week by <strong>bao.long</strong></div>
                  </div>
                  <div style={s("display:flex;align-items:center;gap:var(--space-3);flex:none;align-self:center")}>
                    <span style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>4</span>
                  </div>
                </div>
              </div>
      
              <div style={s("display:flex;align-items:center;gap:var(--space-3);font-size:12px;color:color-mix(in srgb, var(--color-text) 55%, transparent);flex-wrap:wrap")}>
                <button type="button" className="btn btn-secondary">Next page →</button>
                <span>Cursor pagination · 25 per page · 42 matches</span>
              </div>
            </section>
          </>)}
      
          {isTicket && (<>
            <section style={s("padding:var(--space-6) var(--space-4);display:flex;flex-direction:column;gap:var(--space-4)")}>
              <div style={s("display:flex;flex-direction:column;gap:var(--space-2)")}>
                <div style={s("display:flex;gap:var(--space-3);align-items:center;flex-wrap:wrap")}>
                  <h2 style={s("margin:0;flex:1;min-width:260px;text-wrap:pretty")}>Payment settlement stuck for EU merchants <span style={s("color:color-mix(in srgb, var(--color-text) 45%, transparent)")}>#142</span></h2>
                  <div style={s("display:flex;gap:var(--space-2)")}>
                    <button type="button" className="btn btn-secondary">Edit title</button>
                    <button type="button" className="btn btn-primary" onClick={goNew}>New issue</button>
                  </div>
                </div>
                <div style={s("display:flex;gap:var(--space-3);align-items:center;flex-wrap:wrap;font-size:13px;color:color-mix(in srgb, var(--color-text) 65%, transparent)")}>
                  <span style={s("display:inline-flex;align-items:center;gap:6px;background:var(--color-accent);color:var(--color-bg);font-family:var(--font-heading);font-weight:800;font-size:12px;padding:4px 10px")}>
                    <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><circle cx="12" cy="12" r="10"></circle><circle cx="12" cy="12" r="1" fill="currentColor"></circle></svg>Open
                  </span>
                  <span className="tag tag-outline">Bug</span>
                  <span><strong>bao.long</strong> opened this 2 hours ago · 14 comments</span>
                </div>
              </div>
      
              <div style={s("margin:var(--space-2) 0;border:1px solid var(--color-divider);border-radius:var(--radius-md);border-left:2px solid var(--color-accent);background:var(--color-surface);padding:var(--space-3);font-size:13px")}>
                <strong style={s("font-family:var(--font-heading)")}>SLA breached 12 minutes ago.</strong> P0 requires first response within 15 minutes. Escalated to on-call lead <strong>son.truong</strong>.
              </div>
      
              <div style={s(detailGrid)}>
                <div style={s("min-width:0;display:flex;flex-direction:column;gap:var(--space-8)")}>
      
                  <article style={s("border:1px solid var(--color-divider);border-radius:var(--radius-md)")}>
                    <div style={s("display:flex;align-items:center;gap:var(--space-2);padding:var(--space-2) var(--space-3);background:var(--color-surface);border-bottom:1px solid var(--color-divider);font-size:12px")}>
                      <span style={s("width:22px;height:22px;display:grid;place-items:center;border-radius:var(--radius-sm);flex:none;background:var(--color-text);color:var(--color-bg);font-size:10px;font-weight:800")}>B</span>
                      <strong>bao.long</strong><span style={s("color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>opened 2 hours ago</span>
                      <span style={s("margin-left:auto;font-size:10px;letter-spacing:.08em;text-transform:uppercase;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>member</span>
                      <button type="button" className="btn btn-ghost" style={s("font-size:12px")}>Edit</button>
                    </div>
                    <div style={s("padding:var(--space-3);font-size:14px;display:flex;flex-direction:column;gap:var(--space-2)")}>
                      <p style={s("margin:0")}>Settlement batches for EU merchants have not moved out of <code>PENDING</code> since 09:40 UTC. Affected volume so far: 1,284 payouts.</p>
                      <p style={s("margin:0")}><strong>Steps to reproduce:</strong> trigger a settlement run with <code>region=eu-west-1</code> and inspect the outbox — messages are written but never consumed.</p>
                      <p style={s("margin:0")}>Related: #133 (broker credentials), #139 (webhook 401).</p>
                    </div>
                    <div style={s("display:flex;gap:var(--space-2);padding:var(--space-2) var(--space-3);border-top:1px solid var(--color-divider);flex-wrap:wrap")}>
                      <button type="button" className="btn btn-secondary" style={s("font-size:12px")}>👍 6</button>
                      <button type="button" className="btn btn-secondary" style={s("font-size:12px")}>👀 3</button>
                      <button type="button" className="btn btn-secondary" style={s("font-size:12px")}>+</button>
                    </div>
                  </article>
      
                  <div style={s("position:relative;display:flex;flex-direction:column;gap:var(--space-6);border-left:1px solid var(--color-divider);padding-left:var(--space-6)")}>
                    <div className="tlb1" style={s("position:relative;font-size:13px;color:color-mix(in srgb, var(--color-text) 65%, transparent)")}><strong>son.truong</strong> added <span className="tag tag-accent">incident</span> and set priority <strong>P0</strong> · 2 hours ago</div>
                    <div className="tlb1" style={s("position:relative;font-size:13px;color:color-mix(in srgb, var(--color-text) 65%, transparent)")}><strong>son.truong</strong> self-assigned and subscribed · 2 hours ago</div>
      
                    <article className="tlb2" style={s("position:relative;border:1px solid var(--color-divider);border-radius:var(--radius-md)")}>
                      <div style={s("display:flex;align-items:center;gap:var(--space-2);padding:var(--space-2) var(--space-3);background:var(--color-surface);border-bottom:1px solid var(--color-divider);font-size:12px")}>
                        <span style={s("width:22px;height:22px;display:grid;place-items:center;border-radius:var(--radius-sm);flex:none;background:var(--color-text);color:var(--color-bg);font-size:10px;font-weight:800")}>S</span>
                        <strong>son.truong</strong><span style={s("color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>commented 1 hour ago · edited</span>
                      </div>
                      <div style={s("padding:var(--space-3);font-size:14px")}>Broker consumer crashed after the credential rotation. Restarting the settlement worker and replaying the outbox from <code>sequence 918442</code>.</div>
                    </article>
      
                    <article className="tlb3" style={s("position:relative;border:1px solid var(--color-accent);border-radius:var(--radius-md);background:var(--color-accent-100)")}>
                      <div style={s("display:flex;align-items:center;gap:var(--space-2);padding:var(--space-2) var(--space-3);border-bottom:1px solid var(--color-accent);font-size:12px;color:var(--color-accent-800)")}>
                        <span style={s("font-family:var(--font-heading);font-weight:800;font-size:10px;letter-spacing:.08em;text-transform:uppercase")}>Internal note — hidden from customers</span>
                        <strong style={s("margin-left:auto")}>huy.kien</strong>
                        <span>47 minutes ago</span>
                      </div>
                      <div style={s("padding:var(--space-3);font-size:14px;color:var(--color-accent-900)")}>Rotated key was never pushed to the EU vault path. @son.truong please confirm before we tell the merchant anything about root cause.</div>
                    </article>
      
                    <div className="tlb4" style={s("position:relative;font-size:13px;color:color-mix(in srgb, var(--color-text) 65%, transparent)")}>System marked <strong>SLA_BREACHED</strong> and escalated to <strong>son.truong</strong> · 12 minutes ago</div>
                    <div className="tlb1" style={s("position:relative;font-size:13px;color:color-mix(in srgb, var(--color-text) 65%, transparent)")}>#144 was added as a sub-issue · 8 minutes ago</div>
                  </div>
      
                  <div style={s("border:1px solid var(--color-divider);border-radius:var(--radius-md);padding:var(--space-3);display:flex;flex-direction:column;gap:var(--space-2)")}>
                    <div style={s("display:flex;gap:var(--space-4);border-bottom:1px solid var(--color-divider);padding-bottom:var(--space-2);font-family:var(--font-heading);font-weight:800;font-size:12px")}>
                      <span style={s("color:var(--color-accent);border-bottom:2px solid var(--color-accent);padding-bottom:6px;margin-bottom:-9px")}>Write</span>
                      <span style={s("color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>Preview</span>
                      <span style={s("margin-left:auto;font-weight:400;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>Markdown · @mention · #ref</span>
                    </div>
                    <textarea className="input" placeholder="Leave a comment" style={s("min-height:96px")}></textarea>
                    <label className="radio" style={s("font-size:13px")}><input type="checkbox" /><span className="dot"></span>Internal note (customers cannot see this)</label>
                    <div style={s("display:flex;gap:var(--space-2);flex-wrap:wrap;justify-content:flex-end")}>
                      <button type="button" className="btn btn-secondary">Close as completed<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" style={s("opacity:.6;margin-left:2px")}><path d="m6 9 6 6 6-6"></path></svg></button>
                      <button type="button" className="btn btn-primary">Comment</button>
                    </div>
                  </div>
                </div>
      
                <aside style={s("display:flex;flex-direction:column;gap:var(--space-4);border:1px solid var(--color-divider);border-radius:var(--radius-md);padding:var(--space-4)")}>
                  <div style={s("display:flex;flex-direction:column;gap:var(--space-2)")}>
                    <div style={s("display:flex;align-items:center;font-size:11px;letter-spacing:.08em;text-transform:uppercase;color:color-mix(in srgb, var(--color-text) 60%, transparent)")}>Assignees<button type="button" className="btn btn-ghost" style={s("margin-left:auto;font-size:11px")}>Edit</button></div>
                    <div style={s("display:flex;gap:var(--space-2);align-items:center;font-size:13px")}><span style={s("width:22px;height:22px;display:grid;place-items:center;border-radius:var(--radius-sm);flex:none;background:var(--color-text);color:var(--color-bg);font-size:10px;font-weight:800")}>S</span>son.truong</div>
                    <div style={s("display:flex;gap:var(--space-2);align-items:center;font-size:13px")}><span style={s("width:22px;height:22px;display:grid;place-items:center;border-radius:var(--radius-sm);flex:none;background:var(--color-text);color:var(--color-bg);font-size:10px;font-weight:800")}>H</span>huy.kien</div>
                  </div>
                  <div style={s("display:flex;flex-direction:column;gap:var(--space-2)")}>
                    <div style={s("display:flex;align-items:center;font-size:11px;letter-spacing:.08em;text-transform:uppercase;color:color-mix(in srgb, var(--color-text) 60%, transparent)")}>Labels<button type="button" className="btn btn-ghost" style={s("margin-left:auto;font-size:11px")}>Edit</button></div>
                    <div style={s("display:flex;gap:var(--space-1);flex-wrap:wrap")}><span className="tag tag-accent">incident</span><span className="tag tag-neutral">billing</span><span className="tag tag-neutral">eu-region</span></div>
                  </div>
                  <div style={s("display:flex;flex-direction:column;gap:var(--space-2)")}>
                    <div style={s("font-size:11px;letter-spacing:.08em;text-transform:uppercase;color:color-mix(in srgb, var(--color-text) 60%, transparent)")}>Milestone</div>
                    <div style={s("height:6px;border-radius:var(--radius-sm);background:color-mix(in srgb, var(--color-text) 12%, transparent)")}><span style={s("display:block;height:6px;border-radius:var(--radius-sm);width:64%;background:var(--color-accent)")}></span></div>
                    <div style={s("font-size:13px")}>v3.1 Parity <span style={s("color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>· 64% · due 20 Sep</span></div>
                  </div>
                  <div style={s("display:flex;flex-direction:column;gap:var(--space-2)")}>
                    <div style={s("font-size:11px;letter-spacing:.08em;text-transform:uppercase;color:color-mix(in srgb, var(--color-text) 60%, transparent)")}>Priority / SLA</div>
                    <div style={s("display:flex;gap:var(--space-2);align-items:center")}><span className="tag tag-accent" style={s("font-family:var(--font-heading);font-weight:800")}>P0</span><span style={s("font-size:13px;color:var(--color-accent-700)")}>Breached · 12m over</span></div>
                  </div>
                  <div style={s("display:flex;flex-direction:column;gap:var(--space-2)")}>
                    <div style={s("font-size:11px;letter-spacing:.08em;text-transform:uppercase;color:color-mix(in srgb, var(--color-text) 60%, transparent)")}>Relationships</div>
                    <div style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>Sub-issues 1/3 · 33%</div>
                    <div style={s("height:6px;border-radius:var(--radius-sm);background:color-mix(in srgb, var(--color-text) 12%, transparent)")}><span style={s("display:block;height:6px;border-radius:var(--radius-sm);width:33%;background:var(--color-accent)")}></span></div>
                    <div style={s("font-size:13px")}>#143 Replay settlement outbox</div>
                    <div style={s("font-size:13px")}>#144 Notify affected merchants</div>
                    <div style={s("font-size:13px;display:flex;gap:6px;align-items:center")}><span className="tag tag-outline">Blocked by</span>#133</div>
                    <div style={s("display:flex;gap:var(--space-2);flex-wrap:wrap")}><button type="button" className="btn btn-secondary" style={s("font-size:12px")}>+ Sub-issue</button><button type="button" className="btn btn-secondary" style={s("font-size:12px")}>+ Blocked by</button></div>
                  </div>
                  <div style={s("display:flex;flex-direction:column;gap:var(--space-2)")}>
                    <div style={s("font-size:11px;letter-spacing:.08em;text-transform:uppercase;color:color-mix(in srgb, var(--color-text) 60%, transparent)")}>Notifications</div>
                    <button type="button" className="btn btn-secondary btn-block">Unsubscribe</button>
                    <span style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>Subscribed because you were assigned.</span>
                  </div>
                  <div style={s("display:flex;flex-direction:column;gap:var(--space-1)")}>
                    <button type="button" className="btn btn-secondary btn-block">Pin issue</button>
                    <button type="button" className="btn btn-secondary btn-block">Lock conversation</button>
                    <button type="button" className="btn btn-secondary btn-block">Transfer project</button>
                    <button type="button" className="btn btn-secondary btn-block" style={s("color:var(--color-accent)")}>Delete issue</button>
                  </div>
                </aside>
              </div>
            </section>
          </>)}
      
          {isNew && (<>
            <section style={s("padding:var(--space-6) var(--space-4);display:flex;flex-direction:column;gap:var(--space-4)")}>
              <div>
                <div style={s("font-size:11px;letter-spacing:.1em;text-transform:uppercase;color:var(--color-accent)")}>payments-core</div>
                <h2 style={s("margin:0")}>New issue</h2>
              </div>
      
              <div style={s("border-top:1px solid var(--color-divider);padding-top:var(--space-3);display:grid;gap:var(--space-3);grid-template-columns:repeat(auto-fit,minmax(220px,1fr))")}>
                <div style={s("border:1px solid var(--color-accent);border-radius:var(--radius-md);padding:var(--space-3);display:flex;flex-direction:column;gap:6px")}>
                  <div style={s("font-size:10px;letter-spacing:.1em;text-transform:uppercase;color:var(--color-accent)")}>Selected</div>
                  <div style={s("font-family:var(--font-heading);font-weight:800;font-size:17px")}>Incident report</div>
                  <p style={s("margin:0;font-size:13px;opacity:.8")}>Service is degraded or down. Adds <code>incident</code>, type Bug, board Support.</p>
                </div>
                <div style={s("border:1px solid var(--color-divider);border-radius:var(--radius-md);padding:var(--space-3);display:flex;flex-direction:column;gap:6px")}>
                  <div style={s("font-size:10px;letter-spacing:.1em;text-transform:uppercase;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>Template</div>
                  <div style={s("font-family:var(--font-heading);font-weight:800;font-size:17px")}>Customer feedback</div>
                  <p style={s("margin:0;font-size:13px;opacity:.8")}>Complaint or suggestion from a customer. Adds <code>feedback</code>.</p>
                </div>
                <div style={s("border:1px solid var(--color-divider);border-radius:var(--radius-md);padding:var(--space-3);display:flex;flex-direction:column;gap:6px")}>
                  <div style={s("font-size:10px;letter-spacing:.1em;text-transform:uppercase;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>Template</div>
                  <div style={s("font-family:var(--font-heading);font-weight:800;font-size:17px")}>Change request</div>
                  <p style={s("margin:0;font-size:13px;opacity:.8")}>Planned work with a milestone. Adds <code>enhancement</code>.</p>
                </div>
                <div style={s("border:1px dashed var(--color-divider);border-radius:var(--radius-md);padding:var(--space-3);display:flex;flex-direction:column;gap:6px")}>
                  <div style={s("font-size:10px;letter-spacing:.1em;text-transform:uppercase;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>Blank</div>
                  <div style={s("font-family:var(--font-heading);font-weight:800;font-size:17px")}>Open a blank issue</div>
                  <p style={s("margin:0;font-size:13px;opacity:.8")}>Allowed because <code>blank_issues_enabled</code> is on.</p>
                </div>
              </div>
      
              <div style={s(formGrid)}>
                <div style={s("min-width:0;display:flex;flex-direction:column;gap:var(--space-3);border-top:1px solid var(--color-divider);padding-top:var(--space-3)")}>
                  <div className="field"><label htmlFor="ni-title">Title <span style={s("color:var(--color-accent)")}>required</span></label><input className="input" id="ni-title" defaultValue="[Incident]: " /></div>
                  <div className="field">
                    <label htmlFor="ni-what">What happened? <span style={s("color:var(--color-accent)")}>required</span></label>
                    <div style={s("display:flex;gap:var(--space-4);border-bottom:1px solid var(--color-divider);padding-bottom:6px;margin-bottom:6px;font-family:var(--font-heading);font-weight:800;font-size:12px")}>
                      <span style={s("color:var(--color-accent)")}>Write</span><span style={s("color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>Preview</span>
                    </div>
                    <textarea className="input" id="ni-what" placeholder="Impact, affected region, start time…" style={s("min-height:120px")}></textarea>
                  </div>
                  <div className="field"><label htmlFor="ni-steps">Steps to reproduce</label><textarea className="input" id="ni-steps" placeholder="Rendered inside a shell code fence" style={s("min-height:80px")}></textarea></div>
                  <div className="field">
                    <label htmlFor="ni-version">Affected version <span style={s("color:var(--color-accent)")}>required</span></label>
                    <select className="input" id="ni-version"><option>3.1.0</option><option>3.0.4</option><option>3.0.3 (edge)</option></select>
                  </div>
                  <div style={s("display:flex;flex-direction:column;gap:6px")}>
                    <div style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 70%, transparent)")}>Confirmations</div>
                    <label className="radio"><input type="checkbox" /><span className="dot"></span>I searched for a duplicate issue <span style={s("color:var(--color-accent)")}>required</span></label>
                    <label className="radio"><input type="checkbox" /><span className="dot"></span>Customer has been informed</label>
                  </div>
                  <div style={s("display:flex;gap:var(--space-2);justify-content:flex-end;border-top:1px solid var(--color-divider);padding-top:var(--space-3)")}>
                    <button type="button" className="btn btn-secondary">Cancel</button>
                    <button type="button" className="btn btn-primary">Submit new issue</button>
                  </div>
                </div>
      
                <aside style={s("display:flex;flex-direction:column;gap:var(--space-4);padding-top:var(--space-3)")}>
                  <div>
                    <div style={s("font-size:11px;letter-spacing:.08em;text-transform:uppercase;color:color-mix(in srgb, var(--color-text) 60%, transparent);margin-bottom:6px")}>From template</div>
                    <div style={s("display:flex;gap:var(--space-1);flex-wrap:wrap")}><span className="tag tag-accent">incident</span><span className="tag tag-outline">Bug</span></div>
                  </div>
                  <div style={s("display:flex;flex-direction:column;gap:6px")}>
                    <div style={s("font-size:11px;letter-spacing:.08em;text-transform:uppercase;color:color-mix(in srgb, var(--color-text) 60%, transparent)")}>Assignees</div>
                    <button type="button" className="btn btn-secondary btn-block">Assign yourself</button>
                  </div>
                  <div style={s("display:flex;flex-direction:column;gap:6px")}>
                    <div style={s("font-size:11px;letter-spacing:.08em;text-transform:uppercase;color:color-mix(in srgb, var(--color-text) 60%, transparent)")}>Priority</div>
                    <div className="seg"><label className="seg-opt"><input type="radio" name="np" checked={isP0} onChange={pickP0} />P0</label><label className="seg-opt"><input type="radio" name="np" checked={isP1} onChange={pickP1} />P1</label><label className="seg-opt"><input type="radio" name="np" checked={isP2} onChange={pickP2} />P2</label><label className="seg-opt"><input type="radio" name="np" checked={isP3} onChange={pickP3} />P3</label></div>
                    <span style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>{priorityNote}</span>
                  </div>
                  <div style={s("display:flex;flex-direction:column;gap:6px")}>
                    <div style={s("font-size:11px;letter-spacing:.08em;text-transform:uppercase;color:color-mix(in srgb, var(--color-text) 60%, transparent)")}>Need help instead?</div>
                    <a href="#">Contact support chat →</a>
                    <a href="#">Status page →</a>
                  </div>
                </aside>
              </div>
            </section>
          </>)}
      
          {isBoard && (<>
            <section style={s("padding:var(--space-6) var(--space-4);display:flex;flex-direction:column;gap:var(--space-4)")}>
              <div style={s("display:flex;align-items:flex-end;gap:var(--space-4);flex-wrap:wrap")}>
                <div>
                  <div style={s("font-size:11px;letter-spacing:.1em;text-transform:uppercase;color:var(--color-accent)")}>Board · cross-project</div>
                  <h2 style={s("margin:0")}>Incident response</h2>
                </div>
                <div style={s("margin-left:auto;display:flex;gap:var(--space-2)")}>
                  <button type="button" className="btn btn-secondary">Table</button>
                  <button type="button" className="btn btn-secondary" style={s("border-color:var(--color-accent);color:var(--color-accent)")}>Board</button>
                  <button type="button" className="btn btn-secondary">Automation</button>
                </div>
              </div>
              <div style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 60%, transparent);border-top:1px solid var(--color-divider);padding-top:var(--space-2)")}>
                Auto-add <code>is:open label:incident</code> · closed issues move to Done
              </div>
      
              <div style={s("display:grid;gap:var(--space-4);grid-template-columns:repeat(auto-fit,minmax(240px,1fr));align-items:start")}>
                <div style={s("display:flex;flex-direction:column;gap:var(--space-3)")}>
                  <div style={s("display:flex;align-items:center;gap:6px;border-bottom:1px solid var(--color-divider);padding-bottom:6px;font-family:var(--font-heading);font-weight:800;font-size:13px")}>Triage <span style={s("color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>4</span></div>
                  <div style={s("background:var(--color-surface);border:1px solid var(--color-divider);border-radius:var(--radius-md);padding:var(--space-3);display:flex;flex-direction:column;gap:6px")}>
                    <div style={s("font-size:11px;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>payments-core #137</div>
                    <div style={s("font-family:var(--font-heading);font-weight:800;font-size:15px;text-wrap:pretty")}>Refund request not reflected in customer portal</div>
                    <div style={s("display:flex;gap:4px;flex-wrap:wrap")}><span className="tag tag-neutral">feedback</span><span className="tag tag-neutral">P2</span></div>
                  </div>
                  <div style={s("background:var(--color-surface);border:1px solid var(--color-divider);border-radius:var(--radius-md);padding:var(--space-3);display:flex;flex-direction:column;gap:6px")}>
                    <div style={s("font-size:11px;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>Draft item</div>
                    <div style={s("font-family:var(--font-heading);font-weight:800;font-size:15px;text-wrap:pretty")}>Audit vault paths per region</div>
                  </div>
                  <button type="button" className="btn btn-secondary btn-block" style={s("margin-top:0;color:var(--color-accent)")}>+ Add item</button>
                </div>
      
                <div style={s("display:flex;flex-direction:column;gap:var(--space-3)")}>
                  <div style={s("display:flex;align-items:center;gap:6px;border-bottom:2px solid var(--color-accent);padding-bottom:6px;font-family:var(--font-heading);font-weight:800;font-size:13px;color:var(--color-accent)")}>In progress <span style={s("opacity:.7")}>3</span></div>
                  <div style={s("background:var(--color-surface);border:1px solid var(--color-divider);border-radius:var(--radius-md);border-left:2px solid var(--color-accent);padding:var(--space-3);display:flex;flex-direction:column;gap:6px")}>
                    <div style={s("font-size:11px;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>payments-core #142</div>
                    <div style={s("font-family:var(--font-heading);font-weight:800;font-size:15px;text-wrap:pretty")}>Payment settlement stuck for EU merchants</div>
                    <div style={s("display:flex;gap:4px;flex-wrap:wrap")}><span className="tag tag-accent">P0 · SLA breached</span></div>
                    <div style={s("display:flex;gap:4px;align-items:center;font-size:11px;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}><span style={s("width:20px;height:20px;display:grid;place-items:center;border-radius:var(--radius-sm);flex:none;background:var(--color-text);color:var(--color-bg);font-size:9px;font-weight:800")}>S</span>1/3 sub-issues</div>
                  </div>
                  <div style={s("background:var(--color-surface);border:1px solid var(--color-divider);border-radius:var(--radius-md);padding:var(--space-3);display:flex;flex-direction:column;gap:6px")}>
                    <div style={s("font-size:11px;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>payments-core #143</div>
                    <div style={s("font-family:var(--font-heading);font-weight:800;font-size:15px;text-wrap:pretty")}>Replay settlement outbox</div>
                    <div style={s("display:flex;gap:4px;flex-wrap:wrap")}><span className="tag tag-neutral">P1</span></div>
                  </div>
                </div>
      
                <div style={s("display:flex;flex-direction:column;gap:var(--space-3)")}>
                  <div style={s("display:flex;align-items:center;gap:6px;border-bottom:1px solid var(--color-divider);padding-bottom:6px;font-family:var(--font-heading);font-weight:800;font-size:13px")}>Blocked <span style={s("color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>1</span></div>
                  <div style={s("background:var(--color-surface);border:1px solid var(--color-divider);border-radius:var(--radius-md);padding:var(--space-3);display:flex;flex-direction:column;gap:6px")}>
                    <div style={s("font-size:11px;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>web-platform #139</div>
                    <div style={s("font-family:var(--font-heading);font-weight:800;font-size:15px;text-wrap:pretty")}>Webhook deliveries failing with 401</div>
                    <div style={s("display:flex;gap:4px;flex-wrap:wrap")}><span className="tag tag-outline">blocked by #133</span></div>
                  </div>
                </div>
      
                <div style={s("display:flex;flex-direction:column;gap:var(--space-3)")}>
                  <div style={s("display:flex;align-items:center;gap:6px;border-bottom:1px solid var(--color-divider);padding-bottom:6px;font-family:var(--font-heading);font-weight:800;font-size:13px")}>Done <span style={s("color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>12</span></div>
                  <div style={s("background:var(--color-surface);border:1px solid var(--color-divider);border-radius:var(--radius-md);padding:var(--space-3);display:flex;flex-direction:column;gap:6px;opacity:.75")}>
                    <div style={s("font-size:11px;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>payments-core #128</div>
                    <div style={s("font-family:var(--font-heading);font-weight:800;font-size:15px;text-wrap:pretty")}>Latency spike on /api/search/tickets</div>
                    <div style={s("display:flex;gap:4px;flex-wrap:wrap")}><span className="tag tag-neutral">completed</span></div>
                  </div>
                  <div style={s("background:var(--color-surface);border:1px solid var(--color-divider);border-radius:var(--radius-md);padding:var(--space-3);display:flex;flex-direction:column;gap:6px;opacity:.75")}>
                    <div style={s("font-size:11px;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>customer-support #96</div>
                    <div style={s("font-family:var(--font-heading);font-weight:800;font-size:15px;text-wrap:pretty")}>Duplicate refund tickets merged</div>
                    <div style={s("display:flex;gap:4px;flex-wrap:wrap")}><span className="tag tag-neutral">duplicate</span></div>
                  </div>
                </div>
              </div>
            </section>
          </>)}
      
          {isInbox && (<>
            <section style={s("padding:var(--space-6) var(--space-4);display:flex;flex-direction:column;gap:var(--space-4)")}>
              <div style={s("display:flex;align-items:flex-end;gap:var(--space-4);flex-wrap:wrap")}>
                <div>
                  <div style={s("font-size:11px;letter-spacing:.1em;text-transform:uppercase;color:var(--color-accent)")}>7 unread</div>
                  <h2 style={s("margin:0")}>Inbox</h2>
                </div>
                <div style={s("margin-left:auto;display:flex;gap:var(--space-2)")}>
                  <button type="button" className="btn btn-secondary">Mark all read</button>
                  <button type="button" className="btn btn-secondary">Filter<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" style={s("opacity:.6;margin-left:2px")}><path d="m6 9 6 6 6-6"></path></svg></button>
                </div>
              </div>
      
              <div style={s("display:flex;gap:var(--space-4);border-top:1px solid var(--color-divider);border-bottom:1px solid var(--color-divider);padding:var(--space-2) 0;font-family:var(--font-heading);font-weight:800;font-size:13px;flex-wrap:wrap")}>
                <span style={s("color:var(--color-accent)")}>Unread</span>
                <span style={s("color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>Saved</span>
                <span style={s("color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>Done</span>
                <span style={s("margin-left:auto;font-weight:400;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>One thread per issue</span>
              </div>
      
              <div>
                <div style={s("display:flex;gap:var(--space-3);align-items:flex-start;padding:var(--space-3) 0;border-bottom:1px solid var(--color-divider)")}>
                  <span style={s("width:8px;height:8px;background:var(--color-accent);margin-top:8px;flex:none")}></span>
                  <div style={s("flex:1;min-width:0")}>
                    <div style={s("font-size:11px;letter-spacing:.06em;text-transform:uppercase;color:var(--color-accent)")}>Assigned</div>
                    <div style={s("font-family:var(--font-heading);font-weight:800;font-size:15px")}>Payment settlement stuck for EU merchants</div>
                    <div style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>payments-core #142 · SLA breached · 12 minutes ago</div>
                  </div>
                  <div style={s("display:flex;gap:var(--space-1);flex:none;align-self:center")}><button type="button" className="btn btn-secondary" style={s("font-size:12px")}>Done</button><button type="button" className="btn btn-secondary" style={s("font-size:12px")}>Save</button></div>
                </div>
                <div style={s("display:flex;gap:var(--space-3);align-items:flex-start;padding:var(--space-3) 0;border-bottom:1px solid var(--color-divider)")}>
                  <span style={s("width:8px;height:8px;background:var(--color-accent);margin-top:8px;flex:none")}></span>
                  <div style={s("flex:1;min-width:0")}>
                    <div style={s("font-size:11px;letter-spacing:.06em;text-transform:uppercase;color:var(--color-accent)")}>Mentioned</div>
                    <div style={s("font-family:var(--font-heading);font-weight:800;font-size:15px")}>Webhook deliveries failing with 401 after key rotation</div>
                    <div style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>web-platform #139 · huy.kien mentioned you · 47 minutes ago</div>
                  </div>
                  <div style={s("display:flex;gap:var(--space-1);flex:none;align-self:center")}><button type="button" className="btn btn-secondary" style={s("font-size:12px")}>Done</button><button type="button" className="btn btn-secondary" style={s("font-size:12px")}>Save</button></div>
                </div>
                <div style={s("display:flex;gap:var(--space-3);align-items:flex-start;padding:var(--space-3) 0;border-bottom:1px solid var(--color-divider)")}>
                  <span style={s("width:8px;height:8px;border:1px solid var(--color-divider);border-radius:var(--radius-md);margin-top:8px;flex:none")}></span>
                  <div style={s("flex:1;min-width:0")}>
                    <div style={s("font-size:11px;letter-spacing:.06em;text-transform:uppercase;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>Author</div>
                    <div style={s("font-family:var(--font-heading);font-weight:800;font-size:15px;color:color-mix(in srgb, var(--color-text) 75%, transparent)")}>Refund request not reflected in customer portal</div>
                    <div style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>payments-core #137 · 2 new comments · 3 hours ago</div>
                  </div>
                  <div style={s("display:flex;gap:var(--space-1);flex:none;align-self:center")}><button type="button" className="btn btn-secondary" style={s("font-size:12px")}>Done</button><button type="button" className="btn btn-secondary" style={s("font-size:12px")}>Save</button></div>
                </div>
                <div style={s("display:flex;gap:var(--space-3);align-items:flex-start;padding:var(--space-3) 0;border-bottom:1px solid var(--color-divider)")}>
                  <span style={s("width:8px;height:8px;border:1px solid var(--color-divider);border-radius:var(--radius-md);margin-top:8px;flex:none")}></span>
                  <div style={s("flex:1;min-width:0")}>
                    <div style={s("font-size:11px;letter-spacing:.06em;text-transform:uppercase;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>State change</div>
                    <div style={s("font-family:var(--font-heading);font-weight:800;font-size:15px;color:color-mix(in srgb, var(--color-text) 75%, transparent)")}>Latency spike on /api/search/tickets</div>
                    <div style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>payments-core #128 · closed as completed · 3 days ago</div>
                  </div>
                  <div style={s("display:flex;gap:var(--space-1);flex:none;align-self:center")}><button type="button" className="btn btn-secondary" style={s("font-size:12px")}>Done</button><button type="button" className="btn btn-secondary" style={s("font-size:12px")}>Save</button></div>
                </div>
              </div>
            </section>
          </>)}
      
          {isSearch && (<>
            <section style={s("padding:var(--space-6) var(--space-4);display:flex;flex-direction:column;gap:var(--space-4)")}>
              <div>
                <div style={s("font-size:11px;letter-spacing:.1em;text-transform:uppercase;color:var(--color-accent)")}>All projects</div>
                <h2 style={s("margin:0")}>Search</h2>
              </div>
      
              <div style={s("border-top:1px solid var(--color-divider);padding-top:var(--space-3);display:flex;flex-direction:column;gap:var(--space-2)")}>
                <div style={s("display:flex;gap:var(--space-2);flex-wrap:wrap;align-items:center")}>
                  <input className="input" style={s("flex:1;min-width:240px;height:38px;order:1;font-family:ui-monospace,monospace")} defaultValue="is:open label:incident -label:wontfix created:&gt;2026-08-01 sort:reactions-desc" />
                  <select className="input dd" style={s("width:auto;height:38px;order:3;font-size:13px")} aria-label="State"><option>State: open</option><option>State: closed</option><option>State: any</option></select>
                  <select className="input dd" style={s("width:auto;height:38px;order:3;font-size:13px")} aria-label="Reason"><option>Reason: any</option><option>completed</option><option>not planned</option><option>duplicate</option><option>reopened</option></select>
                  <select className="input dd" style={s("width:auto;height:38px;order:3;font-size:13px")} aria-label="Type"><option>Type: any</option><option>Bug</option><option>Task</option><option>Feature</option></select>
                  <select className="input dd" style={s("width:auto;height:38px;order:3;font-size:13px")} aria-label="Priority"><option>Priority: any</option><option>P0</option><option>P1</option><option>P2</option><option>P3</option></select>
                  <select className="input dd" style={s("width:auto;height:38px;order:3;font-size:13px")} aria-label="Sort"><option>Sort: reactions</option><option>created</option><option>updated</option><option>comments</option></select>
                  <button type="button" className="btn btn-primary" style={s("height:38px;order:2")}>Search</button>
                </div>
                <div style={s("display:flex;gap:var(--space-1);flex-wrap:wrap")}>
                  <span className="tag tag-accent qchip">is:open</span><span className="tag tag-accent qchip">label:incident</span><span className="tag tag-outline qchip">-label:wontfix</span><span className="tag tag-neutral qchip">created:&gt;2026-08-01</span><span className="tag tag-neutral qchip">sort:reactions-desc</span>
                </div>
              </div>
      
              <div style={s("display:flex;align-items:baseline;gap:var(--space-3);border-top:1px solid var(--color-divider);padding-top:var(--space-3);flex-wrap:wrap")}>
                <span style={s("font-family:var(--font-heading);font-weight:800;font-size:20px")}>31 results</span>
                <span style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>across 3 projects · capped at 1,000 · 148 ms</span>
              </div>
      
              <table className="table">
                <thead><tr><th>Issue</th><th>Project</th><th>State</th><th>Priority</th><th>Updated</th></tr></thead>
                <tbody>
                  <tr><td><strong>Payment settlement stuck for EU merchants</strong><div style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>#142 · incident, billing</div></td><td>payments-core</td><td>Open</td><td><span className="tag tag-accent">P0</span></td><td>2h ago</td></tr>
                  <tr><td><strong>Webhook deliveries failing with 401</strong><div style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>#139 · integration</div></td><td>web-platform</td><td>Open</td><td><span className="tag tag-neutral">P1</span></td><td>1d ago</td></tr>
                  <tr><td><strong>Duplicate refund tickets merged</strong><div style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>#96 · closed as duplicate of #91</div></td><td>customer-support</td><td>Closed</td><td><span className="tag tag-neutral">P2</span></td><td>4d ago</td></tr>
                  <tr><td><strong>Region failover runbook out of date</strong><div style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>#118 · documentation</div></td><td>payments-core</td><td>Open</td><td><span className="tag tag-neutral">P3</span></td><td>1w ago</td></tr>
                </tbody>
              </table>
            </section>
          </>)}
      
          {isLabels && (<>
            <section style={s("padding:var(--space-6) var(--space-4);display:flex;flex-direction:column;gap:var(--space-4)")}>
              <div style={s("display:flex;align-items:flex-end;gap:var(--space-4);flex-wrap:wrap")}>
                <div>
                  <div style={s("font-size:11px;letter-spacing:.1em;text-transform:uppercase;color:var(--color-accent)")}>payments-core</div>
                  <h2 style={s("margin:0")}>Labels &amp; milestones</h2>
                </div>
                <div style={s("margin-left:auto;display:flex;gap:var(--space-2)")}>
                  <button type="button" className="btn btn-secondary">New label</button>
                  <button type="button" className="btn btn-primary">New milestone</button>
                </div>
              </div>
      
              <div style={s("display:grid;gap:var(--space-8);grid-template-columns:repeat(auto-fit,minmax(300px,1fr))")}>
                <div>
                  <h5 style={s("margin:0 0 var(--space-2)")}>9 labels</h5>
                  <div>
                    <div style={s("display:flex;gap:var(--space-3);align-items:center;padding:var(--space-2) 0;border-bottom:1px solid var(--color-divider)")}>
                      <span className="tag tag-accent">incident</span>
                      <span style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 60%, transparent);flex:1")}>Service degraded or down</span>
                      <span style={s("font-size:12px")}>18 open</span>
                      <button type="button" className="btn btn-ghost" style={s("font-size:12px")}>Edit</button>
                    </div>
                    <div style={s("display:flex;gap:var(--space-3);align-items:center;padding:var(--space-2) 0;border-bottom:1px solid var(--color-divider)")}>
                      <span className="tag tag-neutral">bug</span>
                      <span style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 60%, transparent);flex:1")}>Something is not working</span>
                      <span style={s("font-size:12px")}>24 open</span>
                      <button type="button" className="btn btn-ghost" style={s("font-size:12px")}>Edit</button>
                    </div>
                    <div style={s("display:flex;gap:var(--space-3);align-items:center;padding:var(--space-2) 0;border-bottom:1px solid var(--color-divider)")}>
                      <span className="tag tag-neutral">billing</span>
                      <span style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 60%, transparent);flex:1")}>Payments and invoices</span>
                      <span style={s("font-size:12px")}>11 open</span>
                      <button type="button" className="btn btn-ghost" style={s("font-size:12px")}>Edit</button>
                    </div>
                    <div style={s("display:flex;gap:var(--space-3);align-items:center;padding:var(--space-2) 0;border-bottom:1px solid var(--color-divider)")}>
                      <span className="tag tag-neutral">feedback</span>
                      <span style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 60%, transparent);flex:1")}>Raised by a customer</span>
                      <span style={s("font-size:12px")}>7 open</span>
                      <button type="button" className="btn btn-ghost" style={s("font-size:12px")}>Edit</button>
                    </div>
                    <div style={s("display:flex;gap:var(--space-3);align-items:center;padding:var(--space-2) 0;border-bottom:1px solid var(--color-divider)")}>
                      <span className="tag tag-neutral">wontfix</span>
                      <span style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 60%, transparent);flex:1")}>Will not be worked on</span>
                      <span style={s("font-size:12px")}>0 open</span>
                      <button type="button" className="btn btn-ghost" style={s("font-size:12px")}>Edit</button>
                    </div>
                  </div>
                </div>
      
                <div>
                  <h5 style={s("margin:0 0 var(--space-2)")}>3 open milestones</h5>
                  <div style={s("display:flex;flex-direction:column;gap:var(--space-3)")}>
                    <div style={s("border-bottom:1px solid var(--color-divider);padding-bottom:var(--space-3);display:flex;flex-direction:column;gap:6px")}>
                      <div style={s("display:flex;align-items:baseline;gap:var(--space-2)")}><strong style={s("font-family:var(--font-heading);font-size:17px")}>v3.1 Parity</strong><span style={s("font-size:12px;color:var(--color-accent)")}>due 20 Sep</span></div>
                      <div style={s("height:6px;border-radius:var(--radius-sm);background:color-mix(in srgb, var(--color-text) 12%, transparent)")}><span style={s("display:block;height:6px;border-radius:var(--radius-sm);width:64%;background:var(--color-accent)")}></span></div>
                      <div style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 60%, transparent)")}>64% complete · 16 closed · 9 open</div>
                    </div>
                    <div style={s("border-bottom:1px solid var(--color-divider);padding-bottom:var(--space-3);display:flex;flex-direction:column;gap:6px")}>
                      <div style={s("display:flex;align-items:baseline;gap:var(--space-2)")}><strong style={s("font-family:var(--font-heading);font-size:17px")}>SLA hardening</strong><span style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>due 4 Oct</span></div>
                      <div style={s("height:6px;border-radius:var(--radius-sm);background:color-mix(in srgb, var(--color-text) 12%, transparent)")}><span style={s("display:block;height:6px;border-radius:var(--radius-sm);width:22%;background:var(--color-accent)")}></span></div>
                      <div style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 60%, transparent)")}>22% complete · 4 closed · 14 open</div>
                    </div>
                    <div style={s("display:flex;flex-direction:column;gap:6px")}>
                      <div style={s("display:flex;align-items:baseline;gap:var(--space-2)")}><strong style={s("font-family:var(--font-heading);font-size:17px")}>Customer portal</strong><span style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 55%, transparent)")}>no due date</span></div>
                      <div style={s("height:6px;border-radius:var(--radius-sm);background:color-mix(in srgb, var(--color-text) 12%, transparent)")}><span style={s("display:block;height:6px;border-radius:var(--radius-sm);width:8%;background:var(--color-accent)")}></span></div>
                      <div style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 60%, transparent)")}>8% complete · 1 closed · 12 open</div>
                    </div>
                  </div>
                </div>
              </div>
            </section>
          </>)}
      
          {isSettings && (<>
            <section style={s("padding:var(--space-6) var(--space-4);display:flex;flex-direction:column;gap:var(--space-4)")}>
              <div>
                <div style={s("font-size:11px;letter-spacing:.1em;text-transform:uppercase;color:var(--color-accent)")}>payments-core</div>
                <h2 style={s("margin:0")}>Project settings</h2>
              </div>
      
              <div style={s("padding-top:var(--space-2);display:grid;gap:var(--space-4);grid-template-columns:repeat(auto-fit,minmax(280px,1fr))")}>
                <div style={s("display:flex;flex-direction:column;gap:var(--space-3)")}>
                  <h5 style={s("margin:0")}>Issue policy</h5>
                  <label className="radio"><input type="checkbox" checked={blankIssues} onChange={setBlankIssues} /><span className="dot"></span>Allow blank issues</label>
                  <label className="radio"><input type="checkbox" checked={strictClose} onChange={setStrictClose} /><span className="dot"></span>Strict close policy — block closing a parent with open sub-issues</label>
                  <label className="radio"><input type="checkbox" checked={autoReopen} onChange={setAutoReopen} /><span className="dot"></span>Reopen automatically when a customer comments</label>
                  <div className="field" style={s("margin-top:var(--space-2)")}><label htmlFor="st-slug">Slug</label><input className="input" id="st-slug" defaultValue="payments-core" /></div>
                  <div className="field"><label htmlFor="st-name">Display name</label><input className="input" id="st-name" defaultValue="Payments Core" /></div>
                </div>
      
                <div style={s("display:flex;flex-direction:column;gap:var(--space-3)")}>
                  <h5 style={s("margin:0")}>Contact links</h5>
                  <div style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 60%, transparent)")}>Shown on the template picker instead of creating an issue.</div>
                  <div style={s("border:1px solid var(--color-divider);border-radius:var(--radius-md);padding:var(--space-3);font-size:13px;display:flex;flex-direction:column;gap:4px")}>
                    <strong>Support chat</strong><span style={s("color:color-mix(in srgb, var(--color-text) 60%, transparent)")}>https://chat.example.com — questions about your account</span>
                  </div>
                  <div style={s("border:1px solid var(--color-divider);border-radius:var(--radius-md);padding:var(--space-3);font-size:13px;display:flex;flex-direction:column;gap:4px")}>
                    <strong>Status page</strong><span style={s("color:color-mix(in srgb, var(--color-text) 60%, transparent)")}>https://status.example.com — current uptime</span>
                  </div>
                  <button type="button" className="btn btn-secondary btn-block">+ Add contact link</button>
                </div>
              </div>
      
              <div style={s("padding-top:var(--space-4);display:flex;flex-direction:column;gap:var(--space-3)")}>
                <div style={s("display:flex;align-items:center;gap:var(--space-3);flex-wrap:wrap")}>
                  <h5 style={s("margin:0")}>Webhooks</h5>
                  <button type="button" className="btn btn-primary" style={s("margin-left:auto")}>Add webhook</button>
                </div>
                <table className="table">
                  <thead><tr><th>Target URL</th><th>Events</th><th>Last delivery</th><th>Status</th><th></th></tr></thead>
                  <tbody>
                    <tr><td>https://hooks.slack.com/services/…/incidents</td><td>issues, issue_comment, sla</td><td>34s ago</td><td>200 · 142 ms</td><td><span className="cell-link">Redeliver</span></td></tr>
                    <tr><td>https://crm.example.com/api/tickets</td><td>issues, sub_issues</td><td>6m ago</td><td style={s("color:var(--color-accent-700)")}>401 · attempt 3/5</td><td><span className="cell-link">Redeliver</span></td></tr>
                    <tr><td>https://pager.example.com/hook</td><td>sla</td><td>2h ago</td><td>204 · 88 ms</td><td><span className="cell-link">Redeliver</span></td></tr>
                  </tbody>
                </table>
                <div style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 60%, transparent)")}>Signed with <code>X-Hub-Signature-256</code> · 10 s timeout · retry 1m / 5m / 30m / 2h / 6h then DLQ · deliveries kept 30 days</div>
              </div>
            </section>
          </>)}
      
          {isRbac && (<>
            <section style={s("padding:var(--space-6) var(--space-4);display:flex;flex-direction:column;gap:var(--space-4)")}>
              <div style={s("display:flex;align-items:flex-end;gap:var(--space-4);flex-wrap:wrap")}>
                <div>
                  <div style={s("font-size:11px;letter-spacing:.1em;text-transform:uppercase;color:var(--color-accent)")}>Administration</div>
                  <h2 style={s("margin:0")}>Roles &amp; permissions</h2>
                </div>
                <div style={s("margin-left:auto;display:flex;gap:var(--space-2)")}>
                  <button type="button" className="btn btn-secondary">New permission</button>
                  <button type="button" className="btn btn-primary">New role</button>
                </div>
              </div>
      
              <div style={s("border-top:1px solid var(--color-divider);padding-top:var(--space-3);overflow-x:auto")}>
                <table className="table" style={s("min-width:640px")}>
                  <thead><tr><th>Action</th><th>Customer</th><th>Support</th><th>Responder</th><th>Admin</th></tr></thead>
                  <tbody>
                    <tr><td>ticket.read · timeline (public)</td><td>✓</td><td>✓</td><td>✓</td><td>✓</td></tr>
                    <tr><td>ticket.create · comment · reaction</td><td>✓</td><td>✓</td><td>✓</td><td>✓</td></tr>
                    <tr><td>ticket.internal_note</td><td style={s("color:var(--color-accent)")}>✕</td><td>✓</td><td>✓</td><td>✓</td></tr>
                    <tr><td>ticket.triage · label, milestone, assignee</td><td style={s("color:var(--color-accent)")}>✕</td><td>✓</td><td>✓</td><td>✓</td></tr>
                    <tr><td>ticket.write · lock, pin, transfer</td><td style={s("color:var(--color-accent)")}>✕</td><td style={s("color:var(--color-accent)")}>✕</td><td>✓</td><td>✓</td></tr>
                    <tr><td>ticket.delete</td><td style={s("color:var(--color-accent)")}>✕</td><td style={s("color:var(--color-accent)")}>✕</td><td style={s("color:var(--color-accent)")}>✕</td><td>✓</td></tr>
                    <tr><td>webhook.manage · sla.manage</td><td style={s("color:var(--color-accent)")}>✕</td><td style={s("color:var(--color-accent)")}>✕</td><td style={s("color:var(--color-accent)")}>✕</td><td>✓</td></tr>
                  </tbody>
                </table>
              </div>
      
              <div style={s("border-top:1px solid var(--color-divider);padding-top:var(--space-3);display:flex;flex-direction:column;gap:var(--space-3)")}>
                <div style={s("display:flex;align-items:center;gap:var(--space-3);flex-wrap:wrap")}>
                  <h5 style={s("margin:0")}>Users</h5>
                  <input className="input" style={s("max-width:240px;margin-left:auto")} placeholder="Search users" />
                </div>
                <table className="table">
                  <thead><tr><th>User</th><th>Email</th><th>Roles</th><th>Status</th><th></th></tr></thead>
                  <tbody>
                    <tr><td><strong>son.truong</strong></td><td>son.truong@example.com</td><td><span className="tag tag-neutral">responder</span></td><td>Active</td><td><span className="cell-link">Manage</span></td></tr>
                    <tr><td><strong>huy.kien</strong></td><td>huy.kien@example.com</td><td><span className="tag tag-neutral">support</span></td><td>Active</td><td><span className="cell-link">Manage</span></td></tr>
                    <tr><td><strong>bao.long</strong></td><td>bao.long@example.com</td><td><span className="tag tag-accent">admin</span></td><td>Active</td><td><span className="cell-link">Manage</span></td></tr>
                    <tr><td><strong>ngoc.mai</strong></td><td>ngoc.mai@customer.com</td><td><span className="tag tag-neutral">viewer</span></td><td style={s("color:var(--color-accent-700)")}>Awaiting approval</td><td><span className="cell-link">Manage</span></td></tr>
                  </tbody>
                </table>
              </div>
            </section>
          </>)}
      
          {isSla && (<>
            <section style={s("padding:var(--space-6) var(--space-4);display:flex;flex-direction:column;gap:var(--space-4)")}>
              <div>
                <div style={s("font-size:11px;letter-spacing:.1em;text-transform:uppercase;color:var(--color-accent)")}>Last 7 days</div>
                <h2 style={s("margin:0")}>SLA &amp; escalation</h2>
              </div>
      
              <div style={s("display:grid;gap:var(--space-3);grid-template-columns:repeat(auto-fit,minmax(180px,1fr))")}>
                <div style={s("padding:var(--space-4);border:1px solid var(--color-divider);border-radius:var(--radius-md)")}>
                  <div style={s("font-family:var(--font-heading);font-weight:800;font-size:34px;line-height:1")}>96.2%</div>
                  <div style={s("font-size:11px;letter-spacing:.08em;text-transform:uppercase;color:color-mix(in srgb, var(--color-text) 60%, transparent)")}>Within SLA</div>
                </div>
                <div style={s("padding:var(--space-4);border:1px solid var(--color-divider);border-radius:var(--radius-md)")}>
                  <div style={s("font-family:var(--font-heading);font-weight:800;font-size:34px;line-height:1;color:var(--color-accent)")}>6</div>
                  <div style={s("font-size:11px;letter-spacing:.08em;text-transform:uppercase;color:color-mix(in srgb, var(--color-text) 60%, transparent)")}>Breached</div>
                </div>
                <div style={s("padding:var(--space-4);border:1px solid var(--color-divider);border-radius:var(--radius-md)")}>
                  <div style={s("font-family:var(--font-heading);font-weight:800;font-size:34px;line-height:1")}>11m</div>
                  <div style={s("font-size:11px;letter-spacing:.08em;text-transform:uppercase;color:color-mix(in srgb, var(--color-text) 60%, transparent)")}>Median first response</div>
                </div>
                <div style={s("padding:var(--space-4);border:1px solid var(--color-divider);border-radius:var(--radius-md)")}>
                  <div style={s("font-family:var(--font-heading);font-weight:800;font-size:34px;line-height:1")}>3</div>
                  <div style={s("font-size:11px;letter-spacing:.08em;text-transform:uppercase;color:color-mix(in srgb, var(--color-text) 60%, transparent)")}>Escalated to lead</div>
                </div>
              </div>
      
              <div style={s("display:flex;flex-direction:column;gap:var(--space-3)")}>
                <h5 style={s("margin:var(--space-4) 0 0")}>At risk now</h5>
                <div style={s("display:flex;gap:var(--space-3);align-items:center;min-height:64px;padding:0;border-top:1px solid var(--color-divider);flex-wrap:wrap")}>
                  <span className="tag tag-accent" style={s("font-family:var(--font-heading);font-weight:800")}>Breached 12m</span>
                  <strong style={s("flex:1;min-width:200px")}>#142 Payment settlement stuck for EU merchants</strong>
                  <span style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 60%, transparent)")}>P0 · son.truong · escalated</span>
                  <button type="button" className="btn btn-secondary" style={s("font-size:12px")}>Open</button>
                </div>
                <div style={s("display:flex;gap:var(--space-3);align-items:center;min-height:64px;padding:0;border-top:1px solid var(--color-divider);flex-wrap:wrap")}>
                  <span className="tag tag-outline" style={s("font-family:var(--font-heading);font-weight:800")}>18m left</span>
                  <strong style={s("flex:1;min-width:200px")}>#139 Webhook deliveries failing with 401</strong>
                  <span style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 60%, transparent)")}>P1 · huy.kien</span>
                  <button type="button" className="btn btn-secondary" style={s("font-size:12px")}>Open</button>
                </div>
                <div style={s("display:flex;gap:var(--space-3);align-items:center;min-height:64px;padding:0;border-top:1px solid var(--color-divider);border-bottom:1px solid var(--color-divider);flex-wrap:wrap")}>
                  <span className="tag tag-neutral" style={s("font-family:var(--font-heading);font-weight:800")}>3h 40m left</span>
                  <strong style={s("flex:1;min-width:200px")}>#137 Refund request not reflected in customer portal</strong>
                  <span style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 60%, transparent)")}>P2 · unassigned</span>
                  <button type="button" className="btn btn-secondary" style={s("font-size:12px")}>Open</button>
                </div>
              </div>
      
              <div style={s("display:flex;flex-direction:column;gap:var(--space-3)")}>
                <div style={s("display:flex;align-items:center;gap:var(--space-3);flex-wrap:wrap")}>
                  <h5 style={s("margin:var(--space-4) 0 0")}>Policies</h5>
                  <button type="button" className="btn btn-secondary" style={s("margin-left:auto")}>Edit policies</button>
                </div>
                <table className="table">
                  <thead><tr><th>Priority</th><th>First response</th><th>Resolution</th><th>Escalate after</th><th>Escalate to</th></tr></thead>
                  <tbody>
                    <tr><td><span className="tag tag-accent">P0</span></td><td>15 minutes</td><td>4 hours</td><td>5 minutes</td><td>On-call lead</td></tr>
                    <tr><td><span className="tag tag-neutral">P1</span></td><td>1 hour</td><td>1 day</td><td>30 minutes</td><td>Support lead</td></tr>
                    <tr><td><span className="tag tag-neutral">P2</span></td><td>4 hours</td><td>3 days</td><td>2 hours</td><td>Support lead</td></tr>
                    <tr><td><span className="tag tag-neutral">P3</span></td><td>1 day</td><td>10 days</td><td>—</td><td>—</td></tr>
                  </tbody>
                </table>
                <div style={s("font-size:12px;color:color-mix(in srgb, var(--color-text) 60%, transparent)")}>Scheduler runs every minute and is idempotent — a restart never re-sends a warning.</div>
              </div>
            </section>
          </>)}
      
        </main>
      </div>
    </>
  );
}
