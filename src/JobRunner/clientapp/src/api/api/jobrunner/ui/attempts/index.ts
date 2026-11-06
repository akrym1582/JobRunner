/* eslint-disable */
import type { DefineMethods } from 'aspida';
import type * as Types from '../../../../@types';

export type Methods = DefineMethods<{
  get: {
    query: {
      rootId: string;
      occurrenceId: string;
    };

    status: 200;
    /** OK */
    resBody: Types.AttemptPage;
  };
}>;
