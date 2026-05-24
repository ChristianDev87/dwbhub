import {
  forwardRef,
  type ButtonHTMLAttributes,
  type ReactElement,
  type ReactNode,
  cloneElement,
} from "react";
import type React from "react";
import { cn } from "@/lib/cn";
import { buttonVariants, type ButtonVariantProps } from "./button-variants";

type ButtonOwnProps = ButtonVariantProps & {
  /**
   * Render the button as a different element by passing a JSX element here.
   * The element's props provide defaults; the caller's props (including `children`,
   * `onClick`, etc.) override them. The forwarded ref points to the rendered element
   * — note that HTML attributes like `disabled` have no semantic effect on non-button
   * elements; consumers must add `aria-disabled` / `tabIndex={-1}` manually for those.
   * Use this instead of `asChild` to stay compatible with `@base-ui/react`.
   */
  render?: ReactElement<{ className?: string; children?: ReactNode }>;
};

export type ButtonProps = ButtonHTMLAttributes<HTMLButtonElement> &
  ButtonOwnProps;

export const Button = forwardRef<HTMLElement, ButtonProps>(function Button(
  { className, variant, size, render, children, ...props },
  ref,
) {
  const merged = cn(buttonVariants({ variant, size }), className);

  if (render) {
    const renderClassName = render.props.className;
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    return cloneElement(render as ReactElement<any>, {
      // Element-provided defaults first (e.g. href, target, type) ...
      ...render.props,
      // ... then caller props override (onClick, aria-*, data-*, etc.) ...
      ...props,
      // ... children passed explicitly because they were destructured out of props ...
      children: children ?? render.props.children,
      // ... ref forwarded so consumers retain access to the actual rendered DOM node ...
      ref,
      // ... className merged from both sides last so the variant classes don't get
      // overwritten by either side's raw className.
      className: cn(merged, renderClassName),
    });
  }

  return (
    <button
      ref={ref as React.Ref<HTMLButtonElement>}
      className={merged}
      {...props}
    >
      {children}
    </button>
  );
});
