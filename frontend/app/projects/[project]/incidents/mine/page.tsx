'use client';

import { Guard } from '@/components/ui';
import { IncidentList } from '../IncidentList';

/** Màn hình chính của Responder — UC-BIZ-05 "Việc của tôi". */
export default function MyIncidentsPage() {
  return (
    <Guard permission="incident.read">
      <IncidentList onlyMine />
    </Guard>
  );
}
