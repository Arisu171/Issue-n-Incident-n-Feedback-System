'use client';

import { Guard } from '@/components/ui';
import { IncidentList } from './IncidentList';

export default function IncidentsPage() {
  return (
    <Guard permission="incident.read">
      <IncidentList />
    </Guard>
  );
}
