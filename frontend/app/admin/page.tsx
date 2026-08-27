'use client';

import { useState } from 'react';
import { session } from '@/lib/api';
import { Guard, PageHead } from '@/components/ui';
import { UsersPanel } from './UsersPanel';
import { RolesPanel } from './RolesPanel';
import { tr } from '@/lib/i18n';

type Tab = 'users' | 'roles';

function AdminScreen() {
  const [tab, setTab] = useState<Tab>('users');
  const canReadRoles = session.can('role.read');

  return (
    <>
      <PageHead kicker="Administration" title="Roles & permissions" />

      <div className="tabs">
        <button
          type="button"
          className={tab === 'users' ? 'tab active' : 'tab'}
          onClick={() => setTab('users')}
        >
          {tr('Tài khoản')}
        </button>
        {canReadRoles && (
          <button
            type="button"
            className={tab === 'roles' ? 'tab active' : 'tab'}
            onClick={() => setTab('roles')}
          >
            {tr('Vai trò &amp; quyền')}
          </button>
        )}
      </div>

      {tab === 'users' ? <UsersPanel /> : <RolesPanel />}
    </>
  );
}

export default function AdminPage() {
  return (
    <Guard permission="user.read">
      <AdminScreen />
    </Guard>
  );
}
