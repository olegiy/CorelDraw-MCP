import { useEffect, useRef } from 'react';
import type { components } from '../api.generated';

export default function ApprovalDialog({ pending, decide }: {
  pending: components['schemas']['ConfirmationRequest'] | null;
  decide: (approved: boolean) => void;
}) {
  const dialog = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    if (pending) dialog.current?.showModal();
    else dialog.current?.close();
  }, [pending?.confirmation_id]);
  return <dialog ref={dialog} aria-labelledby="approval-title" onCancel={event => { event.preventDefault(); decide(false); }}>
    <h2 id="approval-title">Подтверждение опасного действия</h2>
    {pending ? <><p>{pending.operation.tool} · {pending.operation.document_id}</p>
      <p>{pending.reasons.join(' ')}</p><pre>{JSON.stringify(pending.operation.arguments, null, 2)}</pre>
      <p>VBA имеет полный доступ к CorelDRAW и ОС. Это не sandbox. Подтверждается только эта неизменённая операция.</p>
      <div className="dialog-actions"><button onClick={() => decide(false)}>Отклонить</button>
        <button className="danger" onClick={() => decide(true)}>Подтвердить один раз</button></div></> : null}
  </dialog>;
}
