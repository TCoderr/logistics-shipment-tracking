import { useId, type InputHTMLAttributes, type ReactNode, type Ref, type SelectHTMLAttributes, type TextareaHTMLAttributes } from "react";

interface FieldShell {
  label: string;
  hint?: string;
  errors?: string[];
}

function Shell({ label, hint, errors, inputId, children }: FieldShell & { inputId: string; children: ReactNode }) {
  return (
    <div className="field">
      <label htmlFor={inputId}>{label}</label>
      {children}
      {hint && (
        <span className="hint" id={`${inputId}-hint`}>
          {hint}
        </span>
      )}
      {errors && errors.length > 0 && (
        <span className="field-error" id={`${inputId}-error`} role="alert">
          {errors.join(" ")}
        </span>
      )}
    </div>
  );
}

function describedBy(inputId: string, hint?: string, errors?: string[]): string | undefined {
  const ids = [hint ? `${inputId}-hint` : null, errors && errors.length > 0 ? `${inputId}-error` : null].filter(Boolean);
  return ids.length > 0 ? ids.join(" ") : undefined;
}

export function TextField({ label, hint, errors, ref, ...input }: FieldShell & InputHTMLAttributes<HTMLInputElement> & { ref?: Ref<HTMLInputElement> }) {
  const generated = useId();
  const inputId = input.id ?? generated;
  return (
    <Shell label={label} hint={hint} errors={errors} inputId={inputId}>
      <input
        type="text"
        {...input}
        ref={ref}
        id={inputId}
        aria-invalid={errors && errors.length > 0 ? true : undefined}
        aria-describedby={describedBy(inputId, hint, errors)}
      />
    </Shell>
  );
}

export function SelectField({ label, hint, errors, children, ...select }: FieldShell & SelectHTMLAttributes<HTMLSelectElement>) {
  const generated = useId();
  const inputId = select.id ?? generated;
  return (
    <Shell label={label} hint={hint} errors={errors} inputId={inputId}>
      <select
        {...select}
        id={inputId}
        aria-invalid={errors && errors.length > 0 ? true : undefined}
        aria-describedby={describedBy(inputId, hint, errors)}
      >
        {children}
      </select>
    </Shell>
  );
}

export function TextAreaField({ label, hint, errors, ...textarea }: FieldShell & TextareaHTMLAttributes<HTMLTextAreaElement>) {
  const generated = useId();
  const inputId = textarea.id ?? generated;
  return (
    <Shell label={label} hint={hint} errors={errors} inputId={inputId}>
      <textarea
        {...textarea}
        id={inputId}
        aria-invalid={errors && errors.length > 0 ? true : undefined}
        aria-describedby={describedBy(inputId, hint, errors)}
      />
    </Shell>
  );
}

/** Filtre çubuğu seçimi: etiket ve kontrol ayrı öğelerdir (erişilebilir ad yalnız etiket metnidir). */
export function FilterSelect({ label, children, ...select }: { label: string } & SelectHTMLAttributes<HTMLSelectElement>) {
  const generated = useId();
  const inputId = select.id ?? generated;
  return (
    <span className="filter">
      <label htmlFor={inputId}>{label}</label>
      <select {...select} id={inputId}>
        {children}
      </select>
    </span>
  );
}

export function FilterDate({ label, ...input }: { label: string } & InputHTMLAttributes<HTMLInputElement>) {
  const generated = useId();
  const inputId = input.id ?? generated;
  return (
    <span className="filter">
      <label htmlFor={inputId}>{label}</label>
      <input {...input} type="date" id={inputId} />
    </span>
  );
}
